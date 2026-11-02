using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tsumi.DeskPet;

/// <summary>聊天输入上行事件回调（由桌宠实现方注册，转发给 AI）。</summary>
public delegate void InputEventCallback(string text);

/// <summary>交互上行事件回调（由桌宠实现方注册，转发给 AI）。</summary>
public delegate void InteractedEventCallback(string text);

/// <summary>模型 Interaction 池里的一条（气泡文本 / 表情 / 动作，动作按名称指定）。</summary>
public record ModelInteractionEntry(string Text, string Exp, string Mtn);
public partial class Live2DDeskPet
{
    public static string ModelRoot { get; } = Path.Combine(AlifePath.StorageFolderPath, "Live2DDeskPet", "Models");
    public static async Task EnsureDefaultModelsAsync()
    {
        if (Directory.Exists(ModelRoot) && Directory.GetDirectories(ModelRoot).Length > 0)
            return;

        const string ModelsZipUrl =
            "https://github.com/BDFFZI/Alife.OfficialPluginStorage/raw/refs/heads/main/Alife.Function.DeskPet/Live2DModel/1.0.0.zip";
        Directory.CreateDirectory(ModelRoot);
        try
        {
            await AlifeUtility.DownloadZipFileAsync(ModelsZipUrl, ModelRoot);
        }
        catch (Exception ex)
        {
            AlifeLog.LogWarning("下载默认 Live2D 桌宠模型失败\n" + ex);
        }
    }
}

/// <summary>
/// Live2D 桌宠显示模块：作为 <see cref="IDeskPet"/> 的一种实现，
/// 自我管理生命周期（在客户端 WebView2 窗口内创建透明窗口渲染 Live2D 模型）。
/// 模型为其内部数据，通过 <see cref="Live2DDeskPetConfig"/> 配置。
/// </summary>
[Module("Live2D 桌宠（Tsumi 增强版）",
    """
    提供一个简易 Live2D 桌宠身体（仅支持 Cubism 3 及以上版本）。
    可选模型下载地址：
    https://github.com/imuncle/live2d
    """,
    defaultCategory: "Tsumi/桌宠模型",
    EditorUI = typeof(Live2DDeskPetUI))]
public partial class Live2DDeskPet(
    IAlifeApplication alifeApplication,
    PluginSystem pluginSystem,
    StorageSystem storageSystem,
    ILogger<Live2DDeskPet> logger) :
    ChatBehaviour,
    IConfigurable<Live2DDeskPetConfig>,
    IDeskPet
{
    public Live2DDeskPetConfig Configuration { get; set; } = null!;

    public event Action<string>? OnInput;
    public event Action<string>? OnInteracted;
    public string[] SupportedExpressions => metadata.Expressions.ToArray();
    public string[] SupportedMotions => metadata.Motions.Keys.ToArray();

    /// <summary>当前使用的模型名。</summary>
    public string CurrentModel => Configuration.ModelName;
    /// <summary>本机可用模型列表（Storage/Live2DDeskPet/Models 下的文件夹名）。
    /// 只列出真能解析到模型描述文件的目录——避免列表里出现"切过去就白屏"的空目录。</summary>
    public string[] AvailableModels => Directory.Exists(ModelRoot)
        ? Directory.GetDirectories(ModelRoot)
            .Where(dir => PetModelMetadata.ResolveModelJsonPath(dir) != null)
            .Select(Path.GetFileName)
            .Where(n => string.IsNullOrEmpty(n) == false)
            .ToArray()!
        : Array.Empty<string>();
    /// <summary>模型切换完成事件（参数为新模型名）。</summary>
    public event Action<string>? ModelSwitched;

    /// <summary>
    /// 切换模型：校验 → 写入配置 → 留在本插件内原地重建桌宠（销毁窗口与模块、按新模型重新加载）。
    /// 刻意不走插件重载，避免卸载正在执行的程序集。
    /// </summary>
    public async Task<bool> SwitchModelAsync(string modelName)
    {
        AlifeLog.LogInformation($"[桌宠] SwitchModelAsync 请求: {(modelName ?? "").Trim()}");
        modelName = (modelName ?? "").Trim();
        if (string.IsNullOrEmpty(modelName)) return false;
        if (AvailableModels.Contains(modelName, StringComparer.OrdinalIgnoreCase) == false)
        {
            AlifeLog.LogWarning($"[桌宠] 模型不存在: {modelName}；可选: {string.Join(", ", AvailableModels)}");
            return false;
        }

        string previous = Configuration.ModelName;
        bool changed = !string.Equals(previous, modelName, StringComparison.OrdinalIgnoreCase);
        Configuration.ModelName = modelName;
        PersistConfig();
        if (!changed) return true;

        AlifeLog.LogInformation($"[桌宠] 开始切换模型：{previous} → {modelName}");
        try
        {
            await SwitchModelInPlaceAsync(modelName);
        }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
            Configuration.ModelName = previous;
            PersistConfig();
            try { await SwitchModelInPlaceAsync(previous); }
            catch (Exception e2) { AlifeLog.LogError(e2); }
            return false;
        }
        AlifeLog.LogInformation($"[桌宠] 模型切换完成：{modelName}");
        ModelSwitched?.Invoke(modelName);
        return true;
    }

    /// <summary>
    /// 原地切换模型：不销毁窗口、不重建 DI、不等空闲，立刻换。
    /// 渲染进程的 pet.js 会移除旧模型再加载新模型；元数据就地替换，各模块立即看到新模型的表情/动作/交互池。
    /// 换模型会让当前气泡重置，正在说话时会被打断——这是刻意的，换来的是秒切。
    /// </summary>
    async Task SwitchModelInPlaceAsync(string modelName)
    {
        if (bridge is null) throw new InvalidOperationException("桌宠还没启动，无法切换模型");

        PetModelMetadata fresh = LoadMetadata(modelName);
        metadata.ReplaceWith(fresh);
        AlifeLog.LogInformation($"[桌宠] 元数据已替换：{fresh.Expressions.Count} 表情 / {fresh.Motions.Count} 动作");

        bridge.SendMessage("load", BuildLoadPayload());
        await Task.CompletedTask;
    }

    /// <summary>水印等固定参数的信封载荷。必须用小写属性名的匿名类型：桥接用 Newtonsoft 默认契约，
    /// 记录类型会原样发出 PascalCase（Id/Value），前端读 pins[i].id 会拿到 undefined。</summary>
    object[] PinPayload() => metadata.Pins
        .Select(pin => (object)new { id = pin.Id, value = pin.Value, auto = pin.Auto })
        .ToArray();

    /// <summary>
    /// 组装发给渲染页加载模型的载荷。额外带上持久化的缩放/偏移/高度倍数，
    /// pet.js 加载模型前先套用，首帧就直接是最终尺寸，避免重载时先按 JS 默认高度倍数(1.1)画一帧再跳变。
    /// </summary>
    object BuildLoadPayload()
    {
        MoveScaleState? ms = provider?.GetService<MoveScaleModule>()?.State;
        return new
        {
            url = new Uri(metadata.ModelPath).AbsoluteUri,
            pins = PinPayload(),
            scale = ms?.Scale ?? 1.0,
            offsetX = ms?.OffsetX ?? 0.0,
            offsetY = ms?.OffsetY ?? 0.0,
            heightRatio = ms?.HeightRatio ?? 1.1,
        };
    }

    /// <summary>按配置解析模型元数据（是否补登记未登记资源、是否去水印都在这里生效）。</summary>
    PetModelMetadata LoadMetadata(string modelName) => PetModelMetadata.Load(
        Path.Combine(ModelRoot, modelName),
        Configuration.MergeUnregisteredAssets,
        Configuration.HideWatermark,
        Configuration.WatermarkParamOverride);

    /// <summary>把当前配置写回配置文件（AI 主动切换后需要持久化）。</summary>
    void PersistConfig()
    {
        try
        {
            string path = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Configuration", $"{GetType().FullName}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafeFile.WriteAllText(path, JsonSerializer.Serialize(Configuration, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    public Task ShowUsing(bool isUsing)
    {
        usingModule.Send(isUsing);
        return Task.CompletedTask;
    }
    public Task ShowSubtitle(string? subtitle)
    {
        if (string.IsNullOrEmpty(subtitle)) subtitleModule.Hide();
        else subtitleModule.Show(subtitle);
        return Task.CompletedTask;
    }
    public Task ShowExpression(string? expression, double? seconds = null)
    {
        expressionModule.PlayExpression(expression, seconds);
        return Task.CompletedTask;
    }
    public Task ShowMotion(string? motion, double? seconds = null)
    {
        if (string.IsNullOrWhiteSpace(motion)) return Task.CompletedTask;
        if (metadata.Motions.TryGetValue(motion, out (string Group, int Index) target) == false) return Task.CompletedTask;
        expressionModule.PlayMotion(target.Group, target.Index, seconds);
        return Task.CompletedTask;
    }
    public Task<Vector2> GetPosition()
    {
        float dpi = (float)window.Dpi;
        Rectangle bounds = window.Bounds;
        Vector2 position = new Vector2(
            (bounds.X + bounds.Width / 2.0f) * dpi,
            (bounds.Y + bounds.Height / 2.0f) * dpi
        );

        return Task.FromResult(position);
    }

    /// <summary>读取桌宠点击区域配置（供 UI 编辑）。模块未就绪时返回空列表。</summary>
    public System.Collections.Generic.List<PetRegionConfig> GetRegions() => regionModule?.GetRegions() ?? new System.Collections.Generic.List<PetRegionConfig>();

    /// <summary>保存桌宠点击区域配置（供 UI 编辑），并刷新前端 overlay。模块未就绪时忽略。</summary>
    public void SaveRegions(System.Collections.Generic.List<PetRegionConfig> regions)
    {
        if (regionModule != null)
            regionModule.SaveRegions(regions);
    }

    /// <summary>读取永久不穿透区（供 UI 编辑）。模块未就绪时返回默认。</summary>
    public PetRegionConfig? GetForeverRegion() => regionModule?.GetForeverRegion();

    /// <summary>保存永久不穿透区（供 UI 编辑），并刷新前端 overlay。模块未就绪时忽略。</summary>
    public void SaveForeverRegion(PetRegionConfig region)
    {
        if (regionModule != null)
            regionModule.SaveForeverRegion(region);
    }

    // ---- 区域坐标 ⇄ 当前窗口真实坐标 换算（供配置 UI 所见即所得）----
    // 区域存储坐标是「模型单位」（相对模型中心）；配置面板显示/输入的是当前窗口内真实坐标。
    // 变换 X/Y 分量独立，逐维换算即可。视图未就绪时原样返回。
    /// <summary>模型单位 X → 当前窗口内真实 X。</summary>
    public double RegionToWindowX(double x) => regionModule is null ? x : regionModule.ToWindow(x, 0).X;
    /// <summary>模型单位 Y → 当前窗口内真实 Y。</summary>
    public double RegionToWindowY(double y) => regionModule is null ? y : regionModule.ToWindow(0, y).Y;
    /// <summary>当前窗口内真实 X → 模型单位 X（保存用）。</summary>
    public double WindowToRegionX(double x) => regionModule is null ? x : regionModule.ToCalibrated(x, 0).X;
    /// <summary>当前窗口内真实 Y → 模型单位 Y（保存用）。</summary>
    public double WindowToRegionY(double y) => regionModule is null ? y : regionModule.ToCalibrated(0, y).Y;
    /// <summary>是否已收到有效模型视图（决定 UI 是否做换算）。</summary>
    public bool HasRegionView => regionModule is not null && regionModule.HasView;

    // ---- 交互开关（与桌宠窗口内的设置面板共用同一份数据）----
    /// <summary>读取全部可配置交互项（供管理界面统一控制）。模块未就绪时返回空列表。</summary>
    public List<Dictionary<string, object?>> GetPetSettings()
    {
        try
        {
            PetInteractionHub? hub = provider?.GetService<PetInteractionHub>();
            return hub?.Snapshot() ?? new List<Dictionary<string, object?>>();
        }
        catch { return new List<Dictionary<string, object?>>(); }
    }

    /// <summary>设置某个交互项的值（"1"/"0" 或数值/选项），并同步刷新桌宠窗口内的设置面板。</summary>
    public void SetPetSetting(string key, string value)
    {
        try
        {
            provider?.GetService<PetInteractionHub>()?.SetValue(key, value);
            provider?.GetService<GestureInteractionModule>()?.PushSettings();
        }
        catch { }
    }

    // ---- 模型自带 Interaction 交互池（原版 8 种交互类型）的编辑 ----

    /// <summary>读取模型 Interaction 各交互类型的可编辑内容（类型/标题/条目：气泡文本+表情+动作）。</summary>
    public List<Dictionary<string, object?>> GetModelInteractionEditor()
    {
        List<Dictionary<string, object?>> list = new();
        foreach ((string Key, string Type, string Label) in PetInteractionHub.InteractionTypes)
        {
            List<Dictionary<string, object?>> entries = new();
            if (metadata != null && metadata.Interactions.TryGetValue(Type, out List<InteractionItem>? pool) && pool != null)
            {
                foreach (InteractionItem item in pool)
                {
                    entries.Add(new Dictionary<string, object?>
                    {
                        ["text"] = item.Text ?? "",
                        ["exp"] = item.Exp ?? "",
                        ["mtn"] = MotionNameOf(item.Mtn),
                    });
                }
            }
            list.Add(new Dictionary<string, object?>
            {
                ["type"] = Type,
                ["key"] = Key,
                ["label"] = Label,
                ["entries"] = entries,
            });
        }
        return list;
    }

    string MotionNameOf(MotionRef? mtn)
    {
        if (mtn == null) return "";
        foreach (KeyValuePair<string, (string Group, int Index)> kv in metadata.Motions)
            if (kv.Value.Group == mtn.Group && kv.Value.Index == mtn.Index) return kv.Key;
        return "";
    }

    /// <summary>
    /// 把编辑过的交互池写回模型文件（先备份 .bak），并就地更新运行时元数据使其立即生效。
    /// 返回空字符串表示成功，否则是错误信息。
    /// </summary>
    public string SaveModelInteractions(Dictionary<string, List<ModelInteractionEntry>> pools)
    {
        try
        {
            // 写回作者原始的模型文件（ModelPath 可能是我们生成的合并文件，不能写到那儿去）
            string path = metadata.SourceModelPath;
            if (string.IsNullOrEmpty(path) || File.Exists(path) == false) return "找不到模型文件";

            JsonObject root = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? new JsonObject();
            JsonObject interaction = new();
            foreach (KeyValuePair<string, List<ModelInteractionEntry>> kv in pools)
            {
                JsonArray arr = new();
                foreach (ModelInteractionEntry e in kv.Value)
                {
                    JsonObject o = new();
                    if (!string.IsNullOrWhiteSpace(e.Text)) o["text"] = e.Text;
                    if (!string.IsNullOrWhiteSpace(e.Exp)) o["exp"] = e.Exp;
                    if (!string.IsNullOrWhiteSpace(e.Mtn) && metadata.Motions.TryGetValue(e.Mtn, out (string Group, int Index) t))
                        o["mtn"] = new JsonObject { ["group"] = t.Group, ["index"] = t.Index };
                    arr.Add(o);
                }
                interaction[kv.Key] = arr;
            }
            root["Interaction"] = interaction;

            File.Copy(path, path + ".bak", true);
            SafeFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // 运行时立即生效（整表换引用，避免中途被读到空表）
            Dictionary<string, List<InteractionItem>> replaced = new();
            foreach (KeyValuePair<string, List<ModelInteractionEntry>> kv in pools)
            {
                List<InteractionItem> items = new();
                foreach (ModelInteractionEntry e in kv.Value)
                {
                    MotionRef? mref = !string.IsNullOrWhiteSpace(e.Mtn) && metadata.Motions.TryGetValue(e.Mtn, out (string Group, int Index) t)
                        ? new MotionRef(t.Group, t.Index)
                        : null;
                    items.Add(new InteractionItem(
                        string.IsNullOrWhiteSpace(e.Text) ? null : e.Text,
                        string.IsNullOrWhiteSpace(e.Exp) ? null : e.Exp,
                        mref));
                }
                replaced[kv.Key] = items;
            }
            metadata.SetInteractions(replaced);
            return "";
        }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
            return "保存失败: " + e.Message;
        }
    }

    public async Task Move(Vector2 offset, float seconds)
    {
        float dpi = (float)window.Dpi;
        Rectangle bounds = window.Bounds;

        double startX = bounds.X;
        double startY = bounds.Y;
        double endX = startX + offset.X / dpi;
        double endY = startY + offset.Y / dpi;
        long startTick = Environment.TickCount64;
        int durationMs = (int)(seconds * 1000);
        if (durationMs <= 0)
            durationMs = 1;

        while (true)
        {
            long elapsed = Environment.TickCount64 - startTick;
            double t = Math.Min(1.0, (double)elapsed / durationMs);
            double ease = t * (2 - t);
            bounds.X = (int)Math.Round(startX + (endX - startX) * ease);
            bounds.Y = (int)Math.Round(startY + (endY - startY) * ease);

            _ = window.SetBoundsAsync(bounds);
            if (t >= 1.0) break;
            await Task.Delay(16);
        }
    }

    /// <summary>将桌宠窗口恢复到默认位置与大小（用于桌宠意外跑出屏幕后重置）。</summary>
    public void ResetWindow()
    {
        window.ResetBounds();
    }

    PetModelMetadata metadata = null!;
    ServiceProvider provider = null!;
    PetWindow window = null!;
    PetBridge bridge = null!;
    SubtitleModule subtitleModule = null!;
    ExpressionModule expressionModule = null!;
    UsingModule usingModule = null!;
    RegionInteractionModule regionModule = null!;

    protected override async Task OnAwake()
    {
        await InitPetAsync();
    }
    protected override async Task OnDestroy()
    {
        if (provider is not null)
            await provider.DisposeAsync();
    }

    /// <summary>初始化（或原地重初始化）桌宠：解析模型元数据 → 建 DI 容器 → 建窗口 → 加载模型与模块。</summary>
    async Task InitPetAsync()
    {
        // 解析模型元数据（无模型时自动下载默认模型）
        await EnsureDefaultModelsAsync();
        string modelName = Configuration.ModelName;
        if (string.IsNullOrWhiteSpace(modelName))
            modelName = "Mao";
        Configuration.ModelName = modelName;
        metadata = LoadMetadata(modelName);

        // 构建依赖注入容器
        provider = BuildProvider(out Type[] moduleTypes);
        try
        {
            //创建窗口
            window = provider.GetRequiredService<PetWindow>();
            bridge = provider.GetRequiredService<PetBridge>();

            // 前端日志转发：pet.js 会把 console.warn/error 与运行时报错发过来，
            // 之前这条链路在主进程侧没人接，前端出的问题完全看不见（参数固定失效就是这么被埋掉的）。
            bridge.OnMessage += (type, payload) =>
            {
                if (type != "log") return;
                string level = payload.TryGetProperty("level", out JsonElement levelProp) ? levelProp.GetString() ?? "" : "";
                string text = payload.TryGetProperty("text", out JsonElement textProp) ? textProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text)) return;
                if (level is "error" or "resource-error" or "unhandledrejection")
                    AlifeLog.LogWarning($"[桌宠前端/{level}] {text}");
                else
                    logger.LogInformation($"[桌宠前端/{level}] {text}");
            };
            string wwwRoot = Path.Combine(pluginSystem.PluginContext.GetPluginDirectoryPath("Tsumi.DeskPet"), "Resources", "Live2DDeskPet");
            await window.CreateAsync(wwwRoot, bridge);

            //加载模型
            await LoadModelAsync();

            //加载模块
            await LoadModuleAsync(moduleTypes);

            subtitleModule = provider.GetRequiredService<SubtitleModule>();
            expressionModule = provider.GetRequiredService<ExpressionModule>();
            usingModule = provider.GetRequiredService<UsingModule>();
            regionModule = provider.GetRequiredService<RegionInteractionModule>();
        }
        catch
        {
            await provider.DisposeAsync();
            provider = null!;
            throw;
        }
    }

    /// <summary>
    /// 构建桌宠组件依赖注入容器：注册窗口、桥、事件回调与全部 IPetModule。
    /// 模块通过反射从程序集自动发现，新增模块类即自动注册，无需手工维护。
    /// </summary>
    ServiceProvider BuildProvider(out Type[] moduleTypes)
    {
        ServiceCollection services = new();
        services.AddSingleton(logger);
        services.AddSingleton(alifeApplication);
        services.AddSingleton(metadata);
        services.AddSingleton(storageSystem);
        services.AddSingleton(new PetStorageKey(Character.StorageKey));
        services.AddSingleton<PetWindow>();
        services.AddSingleton<PetBridge>();
        services.AddSingleton<InputEventCallback>(text => OnInput?.Invoke(text));
        services.AddSingleton<InteractedEventCallback>(text => OnInteracted?.Invoke(text));
        services.AddSingleton<PetInteractionHub>();

        moduleTypes = typeof(IPetModule).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IPetModule).IsAssignableFrom(type))
            .ToArray();
        foreach (Type moduleType in moduleTypes)
            services.AddSingleton(moduleType);

        return services.BuildServiceProvider();
    }
    async Task LoadModelAsync()
    {
        PetBridge bridge = provider.GetRequiredService<PetBridge>();

        TaskCompletionSource loadedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string type, JsonElement _)
        {
            if (type == "loaded")
            {
                bridge.OnMessage -= Handler;
                loadedTcs.TrySetResult();
            }
        }
        bridge.OnMessage += Handler;
        bridge.SendMessage("load", BuildLoadPayload());
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await loadedTcs.Task.WaitAsync(timeout.Token);
    }
    async Task LoadModuleAsync(Type[] moduleTypes)
    {
        IPetModule[] modules = moduleTypes.Select(provider.GetRequiredService).Cast<IPetModule>().ToArray();
        PetBridge bridge = provider.GetRequiredService<PetBridge>();

        List<IPetModule> cssModules = modules.Where(m => m.CssCode != null).ToList();
        if (cssModules.Count > 0)
        {
            string css = string.Join("\n", cssModules.Select(m => m.CssCode));
            string script = $"injectCSS({JsonSerializer.Serialize(css)})";
            await bridge.ExecuteJavaScriptAsync(script);
        }
        foreach (IPetModule module in modules.Where(m => m.HtmlCode != null))
        {
            string script = $"injectHTML({JsonSerializer.Serialize(module.HtmlCode)})";
            await bridge.ExecuteJavaScriptAsync(script);
        }
        foreach (IPetModule module in modules.Where(m => m.JsCode != null))
        {
            await bridge.ExecuteJavaScriptAsync(module.JsCode!);
        }
    }
}
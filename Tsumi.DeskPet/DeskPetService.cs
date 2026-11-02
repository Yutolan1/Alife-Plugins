using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.MessageFilter;

namespace Tsumi.DeskPet;

/// <summary>
/// 桌宠 AI 前端：通过 <see cref="IDeskPet"/> 驱动任意桌宠显示实现（如 Live2D）。
/// 自身不管理桌宠生命周期，桌宠由具体实现（PetServer）自行管理。
/// </summary>
[Module("桌宠控制（Tsumi 增强版）",
    """
    将AI连接到桌宠身体，使其可以用桌宠表达自己并接收互动反馈。
    """,
    defaultCategory: "Tsumi/交互方式",
    EditorUI = typeof(DeskPetServiceUI))]
public class DeskPetService(
    XmlFunctionCaller functionService,
    Interactor<DeskPetService> interactor,
    MessageFilterService messageFilterService,
    IDeskPet pet) :
    ChatBehaviour,
    IConfigurable<DeskPetServiceConfig>
{
    public DeskPetServiceConfig Configuration { get; set; } = null!;

    [XmlFunction(FunctionMode.Content)]
    [Description("显示一段气泡文本")]
    public async Task Speak(XmlExecutorContext context, [XmlContent] string content, CancellationToken cancellationToken)
    {
        try
        {
            switch (context.CallMode)
            {
                case CallMode.Opening:
                    lastBubbleEndTime = 0;
                    break;
                case CallMode.Closing:
                {
                    try
                    {
                        if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < lastBubbleEndTime)
                            await Task.Delay(TimeSpan.FromMilliseconds(lastBubbleEndTime - DateTimeOffset.Now.ToUnixTimeMilliseconds()),
                                cancellationToken);
                    }
                    finally
                    {
                        await pet.ShowSubtitle(null);
                    }
                    break;
                }
                case CallMode.Content:
                {
                    content = content.Trim();
                    if (string.IsNullOrWhiteSpace(content))
                        break;
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < lastBubbleEndTime)
                        await Task.Delay(TimeSpan.FromMilliseconds(lastBubbleEndTime - DateTimeOffset.Now.ToUnixTimeMilliseconds()),
                            cancellationToken);
                    await pet.ShowSubtitle(content);
                    lastBubbleEndTime = DateTimeOffset.Now.ToUnixTimeMilliseconds() +
                                        Math.Max(content.Length * Configuration.BubbleDurationPerCharMs, 1500);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个表情（具体选项见附加说明）。seconds：保持多少秒后自动收回；不填=默认5秒；填0或负数=一直保持")]
    public Task Expression(string option, double? seconds = null)
    {
        option = option.Trim();
        if (string.IsNullOrWhiteSpace(option))
            return Task.CompletedTask;
        if (pet.SupportedExpressions.Contains(option) == false)
            throw new Exception("选项不存在");

        return pet.ShowExpression(option, seconds);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个动作（具体选项见附加说明）。seconds：保持多少秒；不填=自然播放一次；填0或负数=一直保持")]
    public Task Motion(string option, double? seconds = null)
    {
        option = option.Trim();
        if (string.IsNullOrWhiteSpace(option))
            return Task.CompletedTask;
        if (pet.SupportedMotions.Contains(option) == false)
            throw new Exception("选项不存在");

        return pet.ShowMotion(option, seconds);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取当前屏幕位置（使用后需等待结果返回）")]
    public async Task Position()
    {
        try
        {
            Vector2 position = await pet.GetPosition();
            interactor.Poke($"当前位置: x={position.X}, y={position.Y}");
        }
        catch (TimeoutException)
        {
            interactor.Poke("获取坐标超时");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("在屏幕上进行相对移动（注意！该移动方式为相对位置移动，使用前最好先确认当前位置）")]
    public async Task Move(double x = 0, double y = 0, float seconds = 1)
    {
        await pet.Move(new Vector2((float)x, (float)y), seconds);
        Vector2 position = await pet.GetPosition();
        interactor.Poke($"移动成功，当前位置: x={position.X}, y={position.Y}");
    }

    [XmlFunction(FunctionMode.All)]
    [Description("切换桌宠使用的模型。用法：<switchmodel name=\"模型文件夹名\"/> 或 <switchmodel>模型文件夹名</switchmodel>；可选模型见附加说明")]
    public async Task SwitchModel(string? name = null, [XmlContent] string? content = null)
    {
        string want = (string.IsNullOrWhiteSpace(name) ? content : name) ?? "";
        want = want.Trim();
        AlifeLog.LogInformation($"[桌宠] SwitchModel 被调用: name={name ?? ""} content={content ?? ""}");

        string[] models = pet.AvailableModels;
        string? hit = models.FirstOrDefault(m => string.Equals(m, want, StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(m => m.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (hit == null)
        {
            interactor.Poke($"没有找到模型「{want}」。可选模型：{string.Join("、", models)}");
            return;
        }
        if (string.Equals(hit, pet.CurrentModel, StringComparison.OrdinalIgnoreCase))
        {
            interactor.Poke($"现在用的就是「{hit}」");
            return;
        }
        interactor.Poke($"正在切换到模型「{hit}」，稍等一下…");
        AlifeLog.LogInformation($"[桌宠] 阿宵请求切换模型: {hit}（当前 {pet.CurrentModel}）");

        // 后台执行：不占聊天回合；现在是原地秒切，不再等空闲
        string target = hit;
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await pet.SwitchModelAsync(target);
                AlifeLog.LogInformation($"[桌宠] 切换结果: {(ok ? "成功" : "失败")}");
                if (!ok) interactor.Poke($"切换到「{target}」失败");
            }
            catch (Exception e)
            {
                AlifeLog.LogError(e);
                try { interactor.Poke($"切换到「{target}」失败：{e.Message}"); } catch { }
            }
        });
        await Task.CompletedTask;
    }

    long lastBubbleEndTime;
    bool lastStatus;
    XmlHandler? handler;

    protected override Task OnAwake()
    {
        RegisterHandler();

        //注册消息回复规则
        messageFilterService.AddMessageReplyRule(new MessageReplyRule {
            Name = nameof(DeskPetService),
            InputMatching = input => input.Contains(Interactor<DeskPetService>.GetMessageTag()),
            OutputMatching = output => output.Contains(nameof(Speak), StringComparison.OrdinalIgnoreCase),
            CorrectionMessage = () => $"{nameof(DeskPetService)}消息必须用{nameof(Speak)}标签回复。如果不想发送消息，也请发送空标签。"
        }, DestroyCancellationToken);

        return Task.CompletedTask;
    }

    /// <summary>重建并注册函数文档（模型切换后表情/动作/可选模型清单会变，需要重发）。</summary>
    void RegisterHandler()
    {
        if (handler != null)
            functionService.UnregisterHandler(handler);

        string expressions = pet.SupportedExpressions.Length == 0 ? "当前不支持" : string.Join(", ", pet.SupportedExpressions);
        string motions = pet.SupportedMotions.Length == 0 ? "当前不支持" : string.Join(", ", pet.SupportedMotions);
        string models = pet.AvailableModels.Length == 0 ? "无" : string.Join(", ", pet.AvailableModels);

        handler = new XmlHandler(this) {
            Description = "此服务让你获得一副可交互的桌宠身体。这是你主要的对外输出表情动作等外观信息的工具，需要积极使用。",
            Explanation = $"""
                           ## 支持选项
                           - 当前模型：{pet.CurrentModel}
                           - 支持的 {nameof(Expression)} 选项：{expressions}
                           - 支持的 {nameof(Motion)} 选项：{motions}
                           - 可切换的模型：{models}

                           注意：{nameof(Expression)} / {nameof(Motion)} 的 option 只能从上面列出的选项里选。
                           表情/动作都支持 seconds 参数，由你自己决定保持多久：
                           - 不填：表情 5 秒后自动收回默认；动作自然播放一次
                           - seconds="12"：保持 12 秒
                           - seconds="0" 或负数：一直保持（直到你下一次调用）
                           - 例：<expression option="害羞" seconds="0"/> 会一直害羞，直到你换别的表情
                           """
        };
        functionService.RegisterHandler(handler, cancellationToken: DestroyCancellationToken);
    }

    /// <summary>把当前模型的模型名/可切换模型/表情/动作常驻注入上下文（换哪套就常驻哪套）。</summary>
    void RefreshModelPrompt()
    {
        interactor.Prompt(BuildModelInfoText());
    }

    string BuildModelInfoText()
    {
        string expressions = pet.SupportedExpressions.Length == 0 ? "（当前模型没有可用的表情）" : string.Join("、", pet.SupportedExpressions);
        string motions = pet.SupportedMotions.Length == 0 ? "（当前模型没有可用的动作）" : string.Join("、", pet.SupportedMotions);
        string models = pet.AvailableModels.Length == 0 ? "（本机只有这一个模型）" : string.Join("、", pet.AvailableModels);
        return $"""
                【当前桌宠模型】{pet.CurrentModel}
                - 可用表情（{nameof(Expression)} 的 option）：{expressions}
                - 可用动作（{nameof(Motion)} 的 option）：{motions}
                - 可切换的模型（{nameof(SwitchModel)} 的 name）：{models}
                只能用上面列出的选项，不要自己编造；不确定就用{nameof(Speak)}说话。
                想换模型时用 {nameof(SwitchModel)} 指定上面列出的模型名。
                表情/动作的 seconds 由你决定保持多久：不填=表情 5 秒后收回、动作播一次；填秒数=保持那么久；填 0 或负数=一直保持。
                """;
    }

    /// <summary>模型切换时告知阿宵：换成了哪个模型、支持哪些表情与动作。</summary>
    void AnnounceSwitch(string modelName)
    {
        try
        {
            string expressions = pet.SupportedExpressions.Length == 0 ? "无" : string.Join("、", pet.SupportedExpressions);
            string motions = pet.SupportedMotions.Length == 0 ? "无" : string.Join("、", pet.SupportedMotions);
            string models = pet.AvailableModels.Length == 0 ? "无" : string.Join("、", pet.AvailableModels);
            interactor.Chat($"（桌宠换模型了）现在用的是「{modelName}」。可用表情：{expressions}；可用动作：{motions}；还能切换的模型：{models}。");
        }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    string LastModelPath => Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Live2DDeskPet", "LastAnnouncedModel.txt");

    void OnModelSwitched(string modelName)
    {
        AlifeLog.LogInformation($"[桌宠] 模型已切换为 {modelName}，刷新常驻信息与函数文档");
        try { RefreshModelPrompt(); }
        catch (Exception e) { AlifeLog.LogError(e); }
        try { RegisterHandler(); }
        catch (Exception e) { AlifeLog.LogError(e); }
        AnnounceSwitch(modelName);
        WriteLastModel(modelName);
    }

    void WriteLastModel(string modelName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastModelPath)!);
            SafeFile.WriteAllText(LastModelPath, modelName ?? "");
        }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    protected override Task OnStart()
    {
        pet.OnInput += interactor.Chat;
        pet.OnInteracted += text => interactor.Chat("交互：" + text);
        pet.ModelSwitched += OnModelSwitched;

        // 常驻注入当前模型信息（正在用哪套就常驻哪套）
        RefreshModelPrompt();

        // 启动时检测：配置里换过模型（手工改配置/上次切了一半）也要告知一次
        string current = pet.CurrentModel;
        string last = "";
        try { if (File.Exists(LastModelPath)) last = File.ReadAllText(LastModelPath).Trim(); } catch { }
        if (!string.IsNullOrEmpty(last) && !string.Equals(last, current, StringComparison.OrdinalIgnoreCase))
            AnnounceSwitch(current);
        WriteLastModel(current);

        return Task.CompletedTask;
    }
    protected override Task OnUpdate()
    {
        bool currentStatus = ChatBot.IsChatOccupied;
        if (currentStatus != lastStatus)
        {
            lastStatus = currentStatus;
            _ = pet.ShowUsing(currentStatus);
        }

        return Task.CompletedTask;
    }
}
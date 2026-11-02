using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alife.Foundation;

namespace Tsumi.DeskPet;

#region Live2D官方规范扩展

public record ExpressionItem(string? Name, string? File);

public record MotionItem(string? Name, string? File);

public record FileReferencesModel
{
    public List<ExpressionItem> Expressions { get; set; } = new();
    public Dictionary<string, List<MotionItem>> Motions { get; set; } = new();
}

#endregion

#region 自定义的交互反馈

public record MotionRef(string Group, int Index);

public record InteractionItem(string? Text, string? Exp, MotionRef? Mtn);

/// <summary>需要每帧强制固定的参数（水印这类"开关参数"用）。Auto=由模型水印表情自动识别（前端会固定到参数上限），false=手动配置（按给定值固定）。</summary>
public record ParamPin(string Id, double Value, bool Auto = true);

#endregion

/// <summary>
/// Live2D 模型元数据载体与解析器
/// </summary>
public class PetModelMetadata
{
    /// <summary>未登记资源合并后生成的文件名（放在模型目录内，相对路径才能照常解析）。</summary>
    public const string MergedFileName = "_tsumi.merged.model3.json";

    /// <summary>未登记动作归入的组名。</summary>
    public const string ExtraMotionGroup = "额外动作";

    /// <summary>通过 file:// 加载的模型描述文件（有未登记资源被合并时指向生成的合并文件）。</summary>
    public string ModelPath { get; private set; } = string.Empty;

    /// <summary>模型作者写的原始描述文件（保存交互池等改动要写这里，不要写合并文件）。</summary>
    public string SourceModelPath { get; private set; } = string.Empty;

    public List<string> Expressions { get; private set; } = new();
    public Dictionary<string, (string Group, int Index)> Motions { get; private set; } = new();
    public Dictionary<string, List<InteractionItem>> Interactions { get; private set; } = new();

    /// <summary>每帧强制固定的参数值（水印等）。</summary>
    public List<ParamPin> Pins { get; private set; } = new();

    /// <summary>每个动作的自循环与时长（读自动作文件 Meta）：循环的天然"保持"，一次性动作靠按时长重发来保持。</summary>
    public Dictionary<string, (bool Loop, double Duration)> MotionMeta { get; private set; } = new();

    /// <summary>
    /// 就地替换为另一份元数据。模型切换时用：各模块持有的是同一个 metadata 实例，
    /// 直接换掉内部集合的引用即可让它们看到新模型——整体换引用是原子的，
    /// 不会出现「Clear 之后、Add 之前」被别的线程读到空集合的情况（那样会 First() 抛异常）。
    /// </summary>
    public void ReplaceWith(PetModelMetadata other)
    {
        ModelPath = other.ModelPath;
        SourceModelPath = other.SourceModelPath;
        Expressions = other.Expressions;
        Motions = other.Motions;
        Interactions = other.Interactions;
        Pins = other.Pins;
        MotionMeta = other.MotionMeta;
    }

    /// <summary>整体替换交互池（编辑保存时用，同样是原子换引用）。</summary>
    public void SetInteractions(Dictionary<string, List<InteractionItem>> map) => Interactions = map;

    /// <summary>
    /// 定位模型描述文件：目录名同名文件 → 目录内任意 *.model3.json → 一层子目录内任意 *.model3.json。
    /// 后两种兜底是为了兼容"作者打包时多套了一层文件夹"「模型名与文件夹名不一致」的模型包。找不到返回 null。
    /// </summary>
    public static string? ResolveModelJsonPath(string modelDirectory)
    {
        if (Directory.Exists(modelDirectory) == false) return null;
        string modelName = Path.GetFileName(modelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (string pattern in new[] { $"{modelName}.model3.json", $"{modelName}.model.json" })
        {
            string path = Path.Combine(modelDirectory, pattern);
            if (File.Exists(path)) return path;
        }

        foreach (string extension in new[] { "*.model3.json", "*.model.json" })
        {
            string? found = Pick(EnumerateSafely(modelDirectory, extension));
            if (found != null) return found;

            foreach (string sub in EnumerateDirectoriesSafely(modelDirectory))
            {
                found = Pick(EnumerateSafely(sub, extension));
                if (found != null) return found;
            }
        }
        return null;

        // 合并文件是我们自己生成的产物，不能当模型描述文件；多个候选时取文件名最短的（通常就是正主）
        static string? Pick(IEnumerable<string> files)
        {
            List<string> list = files
                .Where(f => Path.GetFileName(f).StartsWith("_tsumi", StringComparison.OrdinalIgnoreCase) == false)
                .OrderBy(f => Path.GetFileName(f).Length)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list.Count > 0 ? list[0] : null;
        }
    }

    static IEnumerable<string> EnumerateSafely(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern); }
        catch { return Array.Empty<string>(); }
    }

    static IEnumerable<string> EnumerateDirectoriesSafely(string directory)
    {
        try { return Directory.EnumerateDirectories(directory); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>从模型目录加载元数据（目录内按规范解析模型描述文件）。</summary>
    /// <param name="mergeUnregisteredAssets">把目录里没在 model3.json 登记的表情/动作文件也纳入可用列表。</param>
    /// <param name="hideWatermark">按模型自带的水印表情自动隐藏水印。</param>
    /// <param name="watermarkOverride">手动指定水印参数固定值，形如 "Param191=1;Param71=1"，留空则用模型自带水印表情里的值。</param>
    public static PetModelMetadata Load(string modelDirectory, bool mergeUnregisteredAssets = true,
        bool hideWatermark = true, string? watermarkOverride = null)
    {
        PetModelMetadata metadata = new();

        string? modelJsonPath = ResolveModelJsonPath(modelDirectory);
        if (modelJsonPath == null)
            return metadata;

        metadata.SourceModelPath = modelJsonPath;
        metadata.ModelPath = modelJsonPath;

        try
        {
            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
            using JsonDocument jsonDoc = JsonDocument.Parse(File.ReadAllText(modelJsonPath));
            JsonElement root = jsonDoc.RootElement;
            string modelJsonDirectory = Path.GetDirectoryName(modelJsonPath)!;

            //收集支持的表情动作
            {
                JsonElement refs = root.TryGetProperty("FileReferences", out JsonElement fileRefs) ? fileRefs : root;
                FileReferencesModel model = JsonSerializer.Deserialize<FileReferencesModel>(refs.GetRawText(), options) ?? new();

                foreach (ExpressionItem exp in model.Expressions)
                {
                    //表情的 Name 并非所有模型的必填项：缺省时退回文件名（与动作的处理保持一致）
                    string name = string.IsNullOrEmpty(exp.Name) ? Stem(exp.File) : exp.Name;
                    if (string.IsNullOrEmpty(name) == false && metadata.Expressions.Contains(name) == false)
                        metadata.Expressions.Add(name);
                }

                foreach ((string groupName, List<MotionItem> items) in model.Motions)
                {
                    for (int index = 0; index < items.Count; index++)
                    {
                        MotionItem item = items[index];
                        string name = string.IsNullOrEmpty(item.Name) ? Stem(item.File) : item.Name; //动作 Name 并不是官方规范，所以备用拿 File 名称
                        if (string.IsNullOrEmpty(name) == false)
                        {
                            metadata.Motions[name] = (groupName, index);
                            metadata.MotionMeta[name] = ReadMotionMeta(modelJsonDirectory, item.File);
                        }
                    }
                }
            }

            // 交互配置
            if (root.TryGetProperty("Interaction", out JsonElement interactJson))
            {
                foreach (JsonProperty poolProp in interactJson.EnumerateObject())
                {
                    metadata.Interactions[poolProp.Name] =
                        JsonSerializer.Deserialize<List<InteractionItem>>(poolProp.Value.GetRawText(), options) ?? new();
                }
            }

            // 目录里躺着、但 model3.json 没登记的表情/动作：按文件名补齐，并合并出一份描述文件供前端加载
            if (mergeUnregisteredAssets)
                MergeUnregisteredAssets(metadata, modelJsonPath, modelJsonDirectory);

            // 水印：模型自带的水印表情只写了参数，得每帧把参数固定住才真的不出现
            if (hideWatermark)
                metadata.Pins = ResolveWatermarkPins(modelJsonDirectory, watermarkOverride);
        }
        catch (Exception e)
        {
            AlifeLog.LogError("Live2D模型解析失败\n" + e);
        }

        return metadata;
    }

    /// <summary>
    /// 把未登记的表情（*.exp3.json）与动作（*.motion3.json）补进描述文件，另存一份合并文件给前端加载。
    /// 原文件保持原样（作者的东西不碰），合并文件每次加载都按目录现状重新生成。
    /// </summary>
    static void MergeUnregisteredAssets(PetModelMetadata metadata, string modelJsonPath, string modelJsonDirectory)
    {
        JsonObject? root = JsonNode.Parse(File.ReadAllText(modelJsonPath)) as JsonObject;
        if (root == null) return;

        JsonObject fileReferences = root["FileReferences"] as JsonObject ?? new JsonObject();
        JsonArray expressions = fileReferences["Expressions"] as JsonArray ?? new JsonArray();
        JsonObject motions = fileReferences["Motions"] as JsonObject ?? new JsonObject();

        // 已登记的文件名（大小写不敏感，只比文件名，够用且不受目录嵌套影响）
        HashSet<string> declaredExpressions = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in expressions)
        {
            string? file = node?["File"]?.GetValue<string>();
            if (string.IsNullOrEmpty(file) == false) declaredExpressions.Add(NormalizeFile(file));
        }

        HashSet<string> declaredMotions = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, JsonNode?> entry in motions)
        {
            if (entry.Value is not JsonArray array) continue;
            foreach (JsonNode? item in array)
            {
                string? file = item?["File"]?.GetValue<string>();
                if (string.IsNullOrEmpty(file) == false) declaredMotions.Add(NormalizeFile(file));
            }
        }

        List<string> extraExpressionFiles = EnumerateSafely(modelJsonDirectory, "*.exp3.json")
            .Where(f => declaredExpressions.Contains(NormalizeFile(Path.GetFileName(f))) == false)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> extraMotionFiles = EnumerateSafely(modelJsonDirectory, "*.motion3.json")
            .Where(f => declaredMotions.Contains(NormalizeFile(Path.GetFileName(f))) == false)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extraExpressionFiles.Count == 0 && extraMotionFiles.Count == 0)
            return;

        foreach (string file in extraExpressionFiles)
        {
            string name = Stem(Path.GetFileName(file));
            if (string.IsNullOrEmpty(name)) continue;
            if (metadata.Expressions.Contains(name) == false)
                metadata.Expressions.Add(name);
            expressions.Add(new JsonObject { ["Name"] = name, ["File"] = Path.GetFileName(file) });
        }

        if (extraMotionFiles.Count > 0)
        {
            JsonArray group = motions[ExtraMotionGroup] as JsonArray ?? new JsonArray();
            int index = group.Count;
            foreach (string file in extraMotionFiles)
            {
                string name = Stem(Path.GetFileName(file));
                if (string.IsNullOrEmpty(name)) continue;
                metadata.Motions[name] = (ExtraMotionGroup, index++);
                metadata.MotionMeta[name] = ReadMotionMeta(modelJsonDirectory, Path.GetFileName(file));
                group.Add(new JsonObject { ["Name"] = name, ["File"] = Path.GetFileName(file) });
            }
            motions[ExtraMotionGroup] = group;
        }

        fileReferences["Expressions"] = expressions;
        fileReferences["Motions"] = motions;
        root["FileReferences"] = fileReferences;

        string mergedPath = Path.Combine(modelJsonDirectory, MergedFileName);
        SafeFile.WriteAllText(mergedPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        metadata.ModelPath = mergedPath;

        AlifeLog.LogInformation($"[桌宠] {Path.GetFileName(modelJsonDirectory)}：补登记 {extraExpressionFiles.Count} 个表情、{extraMotionFiles.Count} 个动作 → {MergedFileName}");
    }

    /// <summary>
    /// 水印参数：模型目录里若带"水印"表情（如 水印.exp3.json），取其参数与值每帧固定，水印即不再出现。
    /// 找不到水印表情就返回空——不猜、不乱动模型。
    /// </summary>
    static List<ParamPin> ResolveWatermarkPins(string modelJsonDirectory, string? watermarkOverride)
    {
        // 手动指定优先（格式 "Param191=1;Param71=1"），用于自动取到的值不好使时调
        if (string.IsNullOrWhiteSpace(watermarkOverride) == false)
        {
            List<ParamPin> manual = new();
            foreach (string part in watermarkOverride.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=', 2);
                if (kv.Length == 2 && double.TryParse(kv[1].Trim(), out double value))
                    manual.Add(new ParamPin(kv[0].Trim(), value, false));
            }
            if (manual.Count > 0)
            {
                AlifeLog.LogInformation($"[桌宠] 水印参数固定（手动配置）：{Describe(manual)}");
                return manual;
            }
        }

        string? watermarkFile = EnumerateSafely(modelJsonDirectory, "*.exp3.json")
            .FirstOrDefault(f =>
            {
                string stem = Stem(Path.GetFileName(f));
                return stem.Contains("水印") || stem.Contains("watermark", StringComparison.OrdinalIgnoreCase);
            });
        if (watermarkFile == null) return new();

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(watermarkFile));
            if (doc.RootElement.TryGetProperty("Parameters", out JsonElement parameters) == false) return new();

            List<ParamPin> pins = new();
            foreach (JsonElement item in parameters.EnumerateArray())
            {
                string? id = item.TryGetProperty("Id", out JsonElement idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                double value = item.TryGetProperty("Value", out JsonElement valueProp) && valueProp.ValueKind == JsonValueKind.Number
                    ? valueProp.GetDouble() : 1;
                pins.Add(new ParamPin(id, value));
            }
            if (pins.Count > 0)
                AlifeLog.LogInformation($"[桌宠] 水印参数固定（来自 {Path.GetFileName(watermarkFile)}）：{Describe(pins)}");
            return pins;
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning($"[桌宠] 水印表情解析失败：{Path.GetFileName(watermarkFile)}\n{e}");
            return new();
        }

        static string Describe(List<ParamPin> pins) => string.Join("、", pins.Select(p => $"{p.Id}={p.Value}"));
    }

    /// <summary>取文件名里的模型资源名（去掉 .exp3.json / .motion3.json 两层后缀）。</summary>
    static string Stem(string? file)
        => string.IsNullOrEmpty(file) ? "" : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));

    static string NormalizeFile(string file) => Path.GetFileName(file).ToLowerInvariant();

    /// <summary>读动作文件的 Meta（是否自循环 / 总时长），供"保持动作"用。</summary>
    static (bool Loop, double Duration) ReadMotionMeta(string modelDirectory, string? file)
    {
        try
        {
            if (string.IsNullOrEmpty(file)) return (false, 0);
            string path = Path.Combine(modelDirectory, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) == false) return (false, 0);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Meta", out JsonElement meta) == false) return (false, 0);
            bool loop = meta.TryGetProperty("Loop", out JsonElement loopProp) && loopProp.ValueKind == JsonValueKind.True;
            double duration = meta.TryGetProperty("Duration", out JsonElement durProp) && durProp.ValueKind == JsonValueKind.Number
                ? durProp.GetDouble() : 0;
            return (loop, duration);
        }
        catch { return (false, 0); }
    }
}

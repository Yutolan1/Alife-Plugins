using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json;

namespace Wv.plugin.Memory;

public record MemoryMeta(int Level, DateTime StartTime, DateTime EndTime)
{
    public string Name => $"{Level}-{StartTime:yyyyMMddHHmmss}-{EndTime:yyyyMMddHHmmss}";
}

/// <summary>
/// 压缩计划：在副本上分析并归纳完成后，用于对真实上下文进行原子替换。
/// </summary>
public record MemoryCompressionPlan(
    int InsertIndex,
    int RemoveCount,
    int NewLevel,
    DateTime StartTime,
    DateTime EndTime,
    string? Summary,
    string FullContent,
    ChatMessageContent HeadAnchor,
    ChatMessageContent TailAnchor);

/// <summary>
/// 记忆核心管理器。协调存储、索引和压缩逻辑。
/// 实现层级化索引关系：每个摘要记录都描述了其涵盖的对话范围和时间跨度。
/// </summary>
public class MemoryManager
{
    internal const string FoldTagCore = "【折叠";

    internal static readonly System.Text.RegularExpressions.Regex PortraitRegex =
        new(@"^# 人物画像.*?(?=^# )", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline);

    internal static readonly System.Text.RegularExpressions.Regex ArchiveDigestRegex =
        new(@"内容概述：\s*```\s*\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>剥掉归档摘要里的人物画像段（10-30 决议：Level&lt;100 块不注入画像）。</summary>
    internal static string StripPortrait(string digest) => PortraitRegex.Replace(digest, "").Trim();

    // ── 实体账本：机器转写精确值（Sawtooth 式），防 LLM 摘要丢值/幻觉 ──
    internal static readonly (string Type, System.Text.RegularExpressions.Regex Pattern)[] EntityPatterns =
    {
        ("路径", new System.Text.RegularExpressions.Regex(@"[A-Za-z]:\\(?:[^\s""'：。、）〕》<>|?/\\]+\\)*[^\s""'：。、）〕》<>|?/\\]+")),
        ("任务ID", new System.Text.RegularExpressions.Regex(@"\b[0-9a-fA-F]{16,64}\b")),
        ("句柄", new System.Text.RegularExpressions.Regex(@"(?i)\bhwnd\s*[=:：]\s*\d{3,12}")),
        ("URL", new System.Text.RegularExpressions.Regex(@"https?://[^\s""'）〉」。]+")),
    };

    /// <summary>从文本抽取精确实体（同批去重）。</summary>
    internal static List<(string Type, string Value)> ExtractEntities(string text)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, rx) in EntityPatterns)
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            {
                string v = m.Value.Trim();
                if (v.Length < 6 || !seen.Add(type + "|" + v)) continue;
                found.Add((type, v));
            }
        return found;
    }

    /// <summary>实体追加进账本（jsonl，跨批按 类型+值 去重）。</summary>
    internal void RecordEntities(string range, List<(string Type, string Value)> entities)
    {
        if (entities.Count == 0) return;
        string dir = Path.Combine(Path.GetDirectoryName(historyStoragePath)!, "model");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "实体账本.jsonl");
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
                try { if (Newtonsoft.Json.Linq.JToken.Parse(line) is Newtonsoft.Json.Linq.JObject o) known.Add(o["类型"] + "|" + o["值"]); } catch { }
        var sb = new StringBuilder();
        foreach (var (type, value) in entities)
        {
            if (!known.Add(type + "|" + value)) continue;
            var o = new Newtonsoft.Json.Linq.JObject
            {
                ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["类型"] = type,
                ["值"] = value,
                ["范围"] = range
            };
            sb.AppendLine(o.ToString(Newtonsoft.Json.Formatting.None));
        }
        if (sb.Length > 0) File.AppendAllText(path, sb.ToString());
    }

    /// <summary>从归档文件文本提取“内容概述”摘要段（不含人物画像）。</summary>
    internal static string? ExtractArchiveDigest(string archiveText)
    {
        var m = ArchiveDigestRegex.Match(archiveText);
        return m.Success ? StripPortrait(m.Groups[1].Value) : null;
    }

    /// <summary>把折叠块还原为全量内容（标记行 + 归档摘要剥画像）；无归档时原样返回并返回 false。</summary>
    internal bool TryUnfold(ChatMessageContent msg, out string full)
    {
        full = msg.Content ?? "";
        var meta = GetMemoryMetaData(msg);
        string archivePath = Path.Combine(Path.GetDirectoryName(historyStoragePath)!, $"L{meta.Level}", meta.Name + ".txt");
        if (!File.Exists(archivePath)) return false;
        try
        {
            string? digest = ExtractArchiveDigest(File.ReadAllText(archivePath));
            if (string.IsNullOrWhiteSpace(digest)) return false;
            full = $"[记忆存档({meta.Name})]\r\n{digest}";
            return true;
        }
        catch { return false; }
    }

    public MemoryManager(HistoryCompressor compressor, TextVectorizer vectorizer, string storagePath,
        int compressionThreshold, int compressionCount, int maxCompressionLevel)
    {
        this.compressionThreshold = compressionThreshold;
        this.compressionCount = compressionCount;
        this.maxCompressionLevel = maxCompressionLevel;

        this.compressor = compressor;
        historyStoragePath = $"{storagePath}/History.json";
        memoryStorage = new MemoryStorage(storagePath, vectorizer);

        if (!Directory.Exists(storagePath))
            Directory.CreateDirectory(storagePath);
    }

    /// <summary>
    /// 阶段一：在快照副本上检查压缩条件并完成LLM归纳，不触碰真实上下文。
    /// 返回压缩计划；无需压缩时返回null。
    /// </summary>
    public async Task<MemoryCompressionPlan?> Analyze(ChatHistory chatHistory)
    {
        //跳过系统提示词
        int contentIndex = 0;
        for (; contentIndex < chatHistory.Count; contentIndex++)
        {
            if (chatHistory[contentIndex].Role != AuthorRole.System)
                break;
        }

        //遍历每个层级的聊天记录
        int areaLevel = int.MaxValue;
        int areaStart = contentIndex;
        int areaCount = 0;
        for (; contentIndex < chatHistory.Count; contentIndex++)
        {
            ChatMessageContent newContent = chatHistory[contentIndex];
            MemoryMeta newMemoryMeta = GetMemoryMetaData(newContent);
            int newLevel = newMemoryMeta.Level;

            if (areaLevel < newLevel)
            {
                //检测到逆序记录，需要合并升级来修复
                if (areaCount == 1)
                {
                    //异常区域仅一条记录，直接升级
                    ChatMessageContent lastContent = chatHistory[contentIndex - 1];
                    MemoryMeta lastMemoryMeta = GetMemoryMetaData(lastContent);
                    return new MemoryCompressionPlan(contentIndex - 1, 1, newLevel, lastMemoryMeta.StartTime,
                        lastMemoryMeta.EndTime, null!, null!, lastContent, lastContent);
                }

                //将异常区域压缩
                return await BuildCompressionPlan(chatHistory, areaStart, areaCount, newLevel, "记忆");
            }

            if (areaLevel != newLevel)
            {
                //正常进入新区域
                areaLevel = newLevel;
                areaStart = contentIndex;
                areaCount = 1;
                continue;
            }

            areaCount++;

            if (areaLevel + 1 <= maxCompressionLevel && areaCount >= (areaLevel == 0 ? compressionThreshold : 4)) //压缩记忆
            {
                int areaCompressionCount = areaLevel == 0 ? compressionCount : 3;
                return await BuildCompressionPlan(chatHistory, areaStart, areaCompressionCount, areaLevel + 1,
                    areaLevel == 0 ? "非记忆存档内容" : $"{areaLevel}级记忆存档");
            }
        }

        return null;

        async Task<MemoryCompressionPlan?> BuildCompressionPlan(ChatHistory snapshot,
            int areaStart, int count, int newLevel, string type)
        {
            //折叠还原：区域内若有折叠块，先从归档回填全文——否则合并 LLM 读到的是头行，高等级摘要会退化
            for (int i = areaStart; i < areaStart + count && i < snapshot.Count; i++)
            {
                if (snapshot[i].Content != null && snapshot[i].Content!.Contains(FoldTagCore)
                    && TryUnfold(snapshot[i], out string full))
                    snapshot[i].Content = full;
            }

            //确认压缩事件段和内容
            DateTime startTime = GetMemoryMetaData(snapshot[areaStart]).StartTime;
            DateTime endTime = GetMemoryMetaData(snapshot[areaStart + count - 1]).EndTime;
            string fullContent = PickContent(snapshot, areaStart, areaStart + count);

            string range = $"从`{startTime}`到`{endTime}`期间的`{type}`";

            // 实体账本：压缩前把区域内的精确值（路径/任务ID/句柄/URL）机器转写入库——摘要丢值也有账可查
            string rangeText = string.Join("\n", snapshot.Skip(areaStart).Take(count).Select(m => m.Content ?? ""));
            var entities = ExtractEntities(rangeText);
            var entityValues = entities.Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
            RecordEntities(range, entities);

            string? summary = await compressor.Compress(new ChatHistoryAgentThread(snapshot), range);
            if (summary == null)
                return null;

            // 防幻觉交叉校验：摘要里的精确值必须能在账本中找到原文出处，否则记警告
            foreach (var hx in ExtractEntities(summary).Where(e => e.Type is "任务ID" or "句柄"))
                if (!entityValues.Contains(hx.Value))
                    Console.WriteLine($"[记忆][实体] 警告：摘要含原文未出现的精确值，疑似幻觉：{hx.Type} {hx.Value}");

            return new MemoryCompressionPlan(areaStart, count, newLevel, startTime, endTime, summary, fullContent,
                snapshot[areaStart], snapshot[areaStart + count - 1]);
        }
    }

    /// <summary>
    /// 阶段二：依据压缩计划对真实上下文进行原子替换（落库+插入存档+移除旧区段）。
    /// </summary>
    public async Task Apply(ChatHistory chatHistory, MemoryCompressionPlan plan)
    {
        //验证替换区域头尾对象未被修改（浅拷贝共享引用，按引用比对）
        if (plan.InsertIndex + plan.RemoveCount > chatHistory.Count
            || !ReferenceEquals(chatHistory[plan.InsertIndex], plan.HeadAnchor)
            || !ReferenceEquals(chatHistory[plan.InsertIndex + plan.RemoveCount - 1], plan.TailAnchor))
        {
            Console.WriteLine("记忆压缩放弃：快照与当前上下文不一致，留待下轮重新分析。");
            return;
        }

        //仅层级升级（逆序单条修复），无需归纳内容
        if (plan.Summary == null)
        {
            ChatMessageContent target = chatHistory[plan.InsertIndex];
            memoryMetaDatas[target] = new MemoryMeta(plan.NewLevel, plan.StartTime, plan.EndTime);
            return;
        }

        await SaveMemory(plan.NewLevel, plan.StartTime, plan.EndTime, plan.Summary, plan.FullContent, chatHistory,
            plan.InsertIndex);

        //移除被压缩记忆
        for (int index = plan.InsertIndex + 1; index <= plan.InsertIndex + plan.RemoveCount; index++)
            memoryMetaDatas.Remove(chatHistory[index]);
        chatHistory.RemoveRange(plan.InsertIndex + 1, plan.RemoveCount);
    }

    public async Task<string> InsertMemory(ChatHistory chatHistory, int level, string summary, string content, DateTime startTime, DateTime endTime)
    {
        // 寻找插入位置（同级区域的最下方）
        int insertIndex = -1;
        int contentIndex = 0;
        // 跳过系统提示词
        for (; contentIndex < chatHistory.Count; contentIndex++)
        {
            if (chatHistory[contentIndex].Role != AuthorRole.System)
                break;
        }

        // 查找该层级的最后一个位置
        bool foundLevel = false;
        for (int i = contentIndex; i < chatHistory.Count; i++)
        {
            int currentLevel = GetMemoryMetaData(chatHistory[i]).Level;
            if (currentLevel == level)
            {
                foundLevel = true;
                insertIndex = i + 1;
            }
            else if (foundLevel)
            {
                // 已经过了该层级的连续区域
                break;
            }
            else if (currentLevel < level)
            {
                // 还没找到该层级，但已经到了更低层级（说明该层级不存在，应插在更低层级之前）
                insertIndex = i;
                break;
            }
        }

        if (insertIndex == -1)
            insertIndex = chatHistory.Count;

        return await SaveMemory(level, startTime, endTime, summary, content, chatHistory, insertIndex);
    }

    public void RemoveMemory(ChatHistory chatHistory, ChatMessageContent content)
    {
        if (chatHistory.Remove(content))
        {
            memoryMetaDatas.Remove(content);
        }
    }

    /// <summary>
    /// 只归档落盘、不插入可压缩上下文（供语义/程序记忆等非时序记忆使用）。
    /// 语义/程序记忆不是「旧对话的压缩」，若像情景一样插进上下文会被 Analyze 误当同级归并，故单独存盘、靠检索/摘录呈现。
    /// </summary>
    public async Task<string> SaveArchiveOnlyAsync(int level, DateTime startTime, DateTime endTime, string summary, string content)
    {
        MemoryMeta memoryMeta = new(level, startTime, endTime);
        string name = memoryMeta.Name;
        await memoryStorage.SaveAsync(name, memoryMeta.Level, summary, content, memoryMeta.StartTime, memoryMeta.EndTime);
        return name;
    }

    public void SaveHistory(ChatHistory chatHistory)
    {
        List<HistoryRecord> history = new List<HistoryRecord>();

        foreach (ChatMessageContent chatMessageContent in chatHistory.Where(content => content.Role != AuthorRole.System))
        {
            if (chatMessageContent.Content == null)
                continue;
            history.Add(new HistoryRecord(
                chatMessageContent.Role,
                chatMessageContent.Content,
                GetMemoryMetaData(chatMessageContent)
            ));
        }

        File.WriteAllText(historyStoragePath, JsonConvert.SerializeObject(history, Formatting.Indented));
    }

    public void LoadHistory(ChatHistory chatHistory)
    {
        if (File.Exists(historyStoragePath) == false)
            return;

        string historyJson = File.ReadAllText(historyStoragePath);
        List<HistoryRecord>? history = JsonConvert.DeserializeObject<List<HistoryRecord>>(historyJson);
        if (history == null)
            return;

        //清除已有的非提示词内容，防止污染
        for (int i = chatHistory.Count - 1; i >= 0; i--)
        {
            if (chatHistory[i].Role != AuthorRole.System)
                chatHistory.RemoveAt(i);
        }

        foreach (HistoryRecord historyRecord in history)
        {
            ChatMessageContent chatMessageContent = new(historyRecord.Role, historyRecord.Content);
            chatHistory.Add(chatMessageContent);
            memoryMetaDatas.Add(chatMessageContent, historyRecord.MemoryMeta);
        }
    }

    public Task<string?> ReadMemory(string index)
    {
        int level = int.Parse(index[..index.IndexOf('-')]);
        return memoryStorage.LoadAsync(level, index);
    }

    public async Task<(List<SearchResult> Results, int Total)> SearchMemory(int level, string keyword, string? question, int count, int offset,
        DateTime? startTime, DateTime? endTime)
    {
        return await memoryStorage.SearchAsync(level, keyword, question, count, offset, startTime, endTime);
    }

    public MemoryMeta GetMemoryMetaData(ChatMessageContent content)
    {
        if (memoryMetaDatas.TryGetValue(content, out MemoryMeta? data) == false)
        {
            data = new MemoryMeta(0, DateTime.Now, DateTime.Now);
            memoryMetaDatas.Add(content, data);
        }

        return data;
    }

    record HistoryRecord(AuthorRole Role, string Content, MemoryMeta MemoryMeta);

    readonly int compressionThreshold;
    readonly int compressionCount;
    readonly int maxCompressionLevel;
    readonly HistoryCompressor compressor;
    readonly MemoryStorage memoryStorage;
    readonly string historyStoragePath;
    readonly Dictionary<ChatMessageContent, MemoryMeta> memoryMetaDatas = new Dictionary<ChatMessageContent, MemoryMeta>();

    async Task<string> SaveMemory(int level, DateTime startTime, DateTime endTime, string summary, string content, ChatHistory chatHistory,
        int insertIndex)
    {
        MemoryMeta memoryMeta = new(level, startTime, endTime);
        string name = memoryMeta.Name;

        //归档到数据库
        await memoryStorage.SaveAsync(name, memoryMeta.Level, summary, content, memoryMeta.StartTime, memoryMeta.EndTime);

        //插入到上下文
        summary = $"""
                   [记忆存档({name})]
                   {summary}
                   """;
        ChatMessageContent compressedContent = new(AuthorRole.Assistant, summary);
        chatHistory.Insert(insertIndex, compressedContent);
        memoryMetaDatas[compressedContent] = new MemoryMeta(level, startTime, endTime);

        Console.WriteLine($"保存记忆：{name}");
        return name;
    }

    string PickContent(ChatHistory chatHistory, int start, int end)
    {
        StringBuilder stringBuilder = new();

        for (int index = start; index < end; index++)
        {
            ChatMessageContent content = chatHistory[index];
            stringBuilder.AppendLine($"【{content.Role}】：\n{content.Content}\n");
        }

        return stringBuilder.ToString();
    }
}
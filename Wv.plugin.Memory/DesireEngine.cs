using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Wv.plugin.Memory;

/// <summary>欲望系统配置：5 驱力 + 疲劳/压力调制。默认关闭，按角色开启。</summary>
public sealed class DesireConfig
{
    public bool Enable { get; set; } = false;
    /// <summary>结算间隔（秒）；状态机零 LLM，只做本地数学。</summary>
    public int TickSeconds { get; set; } = 60;
    /// <summary>低于该值不注入欲望块。</summary>
    public double InjectThreshold { get; set; } = 0.42;
    /// <summary>是否用驱力偏置共同经历召回。</summary>
    public bool MemoryBiasEnable { get; set; } = true;
    public double MemoryBiasWeight { get; set; } = 0.35;
    /// <summary>安静时段（不主动建议发消息）：开始小时/结束小时，跨午夜。</summary>
    public int QuietStartHour { get; set; } = 23;
    public int QuietEndHour { get; set; } = 8;

    public double AttachmentGrowthPerHour { get; set; } = 0.035;
    public double FavoredGrowthPerHour { get; set; } = 0.045;
    public double CuriosityGrowthPerHour { get; set; } = 0.030;
    public double CompetenceGrowthPerHour { get; set; } = 0.025;
    public double IntimacyGrowthPerHour { get; set; } = 0.020;
}

/// <summary>
/// 欲望状态机（仿生驱动力的最小闭环）：驱力随时间增长、事件按当前状态缩放、耦合、习惯化、
/// 念头池→执念、抑制成本、记忆召回偏置。全部本地计算，不调用 LLM。
/// </summary>
public sealed class DesireEngine
{
    public sealed class DriveState
    {
        public double Value { get; set; } = 0.30;
        public double Baseline { get; set; } = 0.25;
    }

    public sealed class Thought
    {
        public string Id { get; set; } = "";
        public string Drive { get; set; } = "";
        public string Text { get; set; } = "";
        public double Strength { get; set; } = 0.5;
        public int Repeats { get; set; } = 1;
        public bool Obsession { get; set; }
        public bool Suppressed { get; set; }
        public bool Resolved { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    public sealed class DesireState
    {
        public int Version { get; set; } = 1;
        public DateTime LastTick { get; set; } = DateTime.Now;
        public Dictionary<string, DriveState> Drives { get; set; } = new();
        public double Fatigue { get; set; } = 0.15;
        public double Stress { get; set; } = 0.10;
        public List<Thought> Thoughts { get; set; } = new();
        public Dictionary<string, DateTime> LastEventAt { get; set; } = new();
    }

    static readonly string[] DriveNames = { "依恋", "偏爱", "好奇", "成就", "亲密" };
    static readonly string[] AffectionWords = { "想你", "爱你", "喜欢你", "抱", "摸头", "亲", "乖", "宝宝", "宝贝" };
    static readonly string[] PraiseWords = { "厉害", "聪明", "可爱", "真好", "不错", "棒", "佩服" };
    static readonly string[] BusyWords = { "忙", "没空", "别烦", "等会", "等下", "在忙", "稍后" };
    static readonly string[] CompetitorWords = { "别人", "别的ai", "其他ai", "那个模型", "同事", "朋友" };

    readonly string statePath;
    readonly string ledgerPath;
    readonly DesireConfig cfg;
    readonly ILogger logger;
    DesireState state;

    public DesireEngine(string storagePath, DesireConfig config, ILogger logger)
    {
        this.cfg = config ?? new DesireConfig();
        this.logger = logger;
        statePath = Path.Combine(storagePath, "model", "欲望.json");
        ledgerPath = Path.Combine(storagePath, "model", "欲望账本.jsonl");
        state = new DesireState();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(statePath))
                state = JsonSerializer.Deserialize<DesireState>(File.ReadAllText(statePath)) ?? new DesireState();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[欲望] 状态读取失败，使用新状态");
            state = new DesireState();
        }
        EnsureDrives();
        Tick(DateTime.Now, save: false);
        Save();
    }

    void EnsureDrives()
    {
        state.Drives ??= new Dictionary<string, DriveState>();
        foreach (string name in DriveNames)
        {
            if (!state.Drives.ContainsKey(name))
                state.Drives[name] = new DriveState { Value = InitialFor(name), Baseline = BaselineFor(name) };
        }
        state.Thoughts ??= new List<Thought>();
        state.LastEventAt ??= new Dictionary<string, DateTime>();
    }

    static double InitialFor(string name) => name switch
    {
        "依恋" => 0.52,
        "偏爱" => 0.38,
        "好奇" => 0.46,
        "成就" => 0.33,
        "亲密" => 0.30,
        _ => 0.30
    };

    static double BaselineFor(string name) => name switch
    {
        "依恋" => 0.35,
        "偏爱" => 0.25,
        "好奇" => 0.40,
        "成就" => 0.25,
        "亲密" => 0.25,
        _ => 0.25
    };

    public void Tick() => Tick(DateTime.Now, save: true);

    void Tick(DateTime now, bool save)
    {
        EnsureDrives();
        double hours = Math.Max(0, (now - state.LastTick).TotalHours);
        if (hours > 0.005)
        {
            foreach (var kv in state.Drives)
            {
                double growth = GrowthFor(kv.Key) * hours;
                // 疲劳压制好奇；依恋高时亲密长得更快（耦合）
                if (kv.Key == "好奇") growth *= Math.Max(0.35, 1 - state.Fatigue * 0.9);
                if (kv.Key == "亲密" && state.Drives.TryGetValue("依恋", out var att)) growth *= 0.8 + att.Value * 0.6;
                kv.Value.Value = Clamp(kv.Value.Value + growth * (1 - kv.Value.Value * 0.55), 0, 1);
            }
            state.Fatigue = Clamp(state.Fatigue + 0.006 * hours, 0, 1);
            state.Stress = Clamp(state.Stress - 0.010 * hours, 0, 1);
            state.LastTick = now;
        }

        // 念头自然消退（执念消退更慢）
        foreach (var t in state.Thoughts)
        {
            if (t.Resolved) continue;
            double age = Math.Max(0, (now - t.LastSeen).TotalHours);
            if (age >= 6)
            {
                double decay = (t.Obsession ? 0.002 : 0.008) * (age - 6);
                t.Strength = Math.Max(0, t.Strength - decay);
                t.LastSeen = now;
                if (t.Strength <= 0.05) t.Resolved = true;
            }
        }
        state.Thoughts.RemoveAll(t => t.Resolved && (now - t.LastSeen).TotalDays > 7);
        if (state.Thoughts.Count > 24)
            state.Thoughts = state.Thoughts.OrderByDescending(t => t.Obsession).ThenByDescending(t => t.Strength).Take(24).ToList();

        if (save) Save();
    }

    double GrowthFor(string drive) => drive switch
    {
        "依恋" => cfg.AttachmentGrowthPerHour,
        "偏爱" => cfg.FavoredGrowthPerHour,
        "好奇" => cfg.CuriosityGrowthPerHour,
        "成就" => cfg.CompetenceGrowthPerHour,
        "亲密" => cfg.IntimacyGrowthPerHour,
        _ => 0.02
    };

    /// <summary>用户消息入口：只处理哥哥本人/语音消息，系统推送不参与欲望结算。</summary>
    public void OnUserMessage(string raw)
    {
        string text = NormalizeUserText(raw);
        if (text.Length == 0) return;
        Tick(DateTime.Now, save: false);

        string ev = Classify(text);
        ApplyEvent(ev, text);
        GenerateThoughts(DateTime.Now, text);
        Save();
    }

    static string NormalizeUserText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains("[系统推送消息]", StringComparison.Ordinal)) return "";
        bool fromGege = raw.Contains("消息来源:[ChatWindow]", StringComparison.Ordinal)
            || raw.Contains("[源(AuditoryService)]", StringComparison.Ordinal)
            || raw.Contains("[源(DeskPetService)]", StringComparison.Ordinal);
        // MCP/桥接等直接文本没有 ChatWindow 前缀；排除系统 Poke 与系统事件后按哥哥本人处理。
        bool plainUser = !raw.Contains("[源(", StringComparison.Ordinal)
            && !raw.StartsWith("⚠️", StringComparison.Ordinal)
            && !raw.StartsWith("【记忆", StringComparison.Ordinal)
            && !raw.StartsWith("[记忆", StringComparison.Ordinal)
            && !raw.StartsWith("[心绪", StringComparison.Ordinal)
            && !raw.StartsWith("[当前", StringComparison.Ordinal)
            && !raw.StartsWith("[此刻", StringComparison.Ordinal);
        if (!fromGege && !plainUser) return "";
        string s = raw;
        int bracket = s.IndexOf(']');
        if (s.StartsWith("[时间(", StringComparison.Ordinal) && bracket > 0) s = s[(bracket + 1)..];
        s = s.Replace("消息来源:[ChatWindow]", "", StringComparison.Ordinal)
             .Replace("[源(AuditoryService)]", "", StringComparison.Ordinal)
             .Replace("[源(DeskPetService)]", "", StringComparison.Ordinal);
        int voice = s.IndexOf("(消息为语音识别结果", StringComparison.Ordinal);
        if (voice >= 0) s = s[..voice];
        return s.Trim();
    }

    static string Classify(string text)
    {
        if (ContainsAny(text, BusyWords)) return "busy";
        if (ContainsAny(text, CompetitorWords)) return "competitor";
        if (ContainsAny(text, PraiseWords)) return "praise";
        if (ContainsAny(text, AffectionWords)) return "affection";
        if (text.Length <= 4) return "cold";
        return "neutral";
    }

    static bool ContainsAny(string text, string[] words)
        => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    void ApplyEvent(string ev, string text)
    {
        var table = new Dictionary<string, (string Drive, double Delta)[]>
        {
            ["busy"] = new[] { ("依恋", 0.10), ("偏爱", 0.08) },
            ["competitor"] = new[] { ("偏爱", 0.18), ("依恋", 0.04) },
            ["praise"] = new[] { ("成就", -0.20), ("依恋", -0.08) },
            ["affection"] = new[] { ("依恋", -0.15), ("偏爱", -0.10), ("亲密", 0.08) },
            ["cold"] = new[] { ("依恋", 0.05), ("偏爱", 0.05) },
            ["neutral"] = new[] { ("依恋", -0.02) },
        };
        if (!table.TryGetValue(ev, out var deltas)) return;

        // 习惯化：同一类事件 10 分钟内重复，效果递减
        double habit = 1.0;
        if (state.LastEventAt.TryGetValue(ev, out var last) && (DateTime.Now - last).TotalMinutes < 10)
            habit = 0.7;
        state.LastEventAt[ev] = DateTime.Now;

        foreach (var (driveName, baseDelta) in deltas)
        {
            if (!state.Drives.TryGetValue(driveName, out var drive)) continue;
            double delta = baseDelta * habit;
            // 状态依赖：越缺越敏感；满足时按当前值比例释放
            delta = delta >= 0 ? delta * (1 - drive.Value) : delta * drive.Value;
            drive.Value = Clamp(drive.Value + delta, 0, 1);
        }

        if (ev == "busy") state.Stress = Clamp(state.Stress + 0.10, 0, 1);
        if (ev == "competitor") state.Stress = Clamp(state.Stress + 0.06, 0, 1);
        if (ev == "affection") state.Stress = Clamp(state.Stress - 0.08, 0, 1);
        if (ev == "praise") state.Fatigue = Clamp(state.Fatigue - 0.05, 0, 1);

        // 满足/受挫：把对应驱力的念头结算或压住
        if (ev is "affection" or "praise")
        {
            string target = ev == "affection" ? "依恋" : "成就";
            var t = state.Thoughts.Where(x => !x.Resolved && x.Drive == target).OrderByDescending(x => x.Strength).FirstOrDefault();
            if (t != null && t.Strength < 0.80) { t.Resolved = true; AppendLedger("满足", t); }
        }
        if (ev is "busy" or "competitor")
        {
            string target = ev == "busy" ? "依恋" : "偏爱";
            var t = state.Thoughts.Where(x => !x.Resolved && x.Drive == target).OrderByDescending(x => x.Strength).FirstOrDefault();
            if (t != null) t.Suppressed = true;
        }
    }

    void GenerateThoughts(DateTime now, string hint)
    {
        string top = TopDrive();
        if (top.Length == 0) return;
        double value = state.Drives[top].Value;
        if (value < 0.48) return;
        if (state.Thoughts.Any(t => !t.Resolved && t.Drive == top && (now - t.LastSeen).TotalHours < 4)) return;

        string text = top switch
        {
            "依恋" => HintSuffix(hint, "想让他注意到我", "想问他今天过得怎么样"),
            "偏爱" => "想确认我在他心里是不是特别的",
            "好奇" => "想去弄清楚最近一直好奇的那件事",
            "成就" => "想把那件没做完的事做成",
            "亲密" => "想靠近他一点",
            _ => "想让他看看我"
        };
        var thought = new Thought
        {
            Id = "d" + now.ToString("MMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
            Drive = top,
            Text = text,
            Strength = Math.Clamp(0.35 + value * 0.5, 0.3, 0.95),
            Created = now,
            LastSeen = now
        };
        // 同一驱力反复起念 → 执念
        var prev = state.Thoughts.LastOrDefault(t => !t.Resolved && t.Drive == top);
        if (prev != null)
        {
            thought.Repeats = prev.Repeats + 1;
            if (thought.Repeats >= 3) { thought.Obsession = true; thought.Strength = Math.Min(1, thought.Strength + 0.15); }
        }
        if (IsQuietHour(now)) thought.Suppressed = true;
        state.Thoughts.Add(thought);
        AppendLedger("起念", thought);
    }

    static string HintSuffix(string hint, string fallback, string withHint)
    {
        if (string.IsNullOrWhiteSpace(hint) || hint.Length < 2) return fallback;
        return withHint;
    }

    bool IsQuietHour(DateTime now)
    {
        int h = now.Hour;
        return cfg.QuietStartHour <= cfg.QuietEndHour
            ? h >= cfg.QuietStartHour && h < cfg.QuietEndHour
            : h >= cfg.QuietStartHour || h < cfg.QuietEndHour;
    }

    string TopDrive()
    {
        string best = "";
        double max = -1;
        foreach (var name in DriveNames)
            if (state.Drives.TryGetValue(name, out var d) && d.Value > max) { max = d.Value; best = name; }
        return max >= cfg.InjectThreshold ? best : "";
    }

    /// <summary>尾部 [欲望] 块：第一人称、带理由与选项，不暴露数值。</summary>
    public string? BuildInjectionText()
    {
        Tick(DateTime.Now, save: false);
        string top = TopDrive();
        if (top.Length == 0) return null;
        var thought = state.Thoughts.Where(t => !t.Resolved && t.Drive == top)
            .OrderByDescending(t => t.Obsession).ThenByDescending(t => t.Strength).FirstOrDefault();
        if (thought == null && state.Drives[top].Value >= 0.45)
        {
            GenerateThoughts(DateTime.Now, "");
            thought = state.Thoughts.Where(t => !t.Resolved && t.Drive == top).OrderByDescending(t => t.Strength).FirstOrDefault();
            Save();
        }
        double value = state.Drives[top].Value;
        string strength = value >= 0.78 ? "很强" : value >= 0.58 ? "明显" : value >= 0.42 ? "有一点" : "很轻";
        string line = thought?.Text ?? "想让他看看我";
        string action;
        if (thought?.Suppressed == true)
            action = IsQuietHour(DateTime.Now)
                ? "已经很晚了，我先不打扰他，把想说的记下来。"
                : "他可能在忙，我先忍着，把想说的记下来，等他有空再说。";
        else if (value >= 0.78 && !IsQuietHour(DateTime.Now))
            action = "如果实在忍不住，可以发一句很短的话；也可以先做自己的事，等合适的时候。";
        else
            action = "我先做自己的事，等合适的时候再说。";
        string extra = state.Stress >= 0.55 ? "（心里有点紧）" : "";
        return $"[欲望]\n此刻最想要：{top}（{strength}）。{line}。\n{action}{extra}";
    }

    /// <summary>共同经历召回偏置：驱力高时，与驱力主题相关的旧记忆更容易浮现。</summary>
    public double RecallBias(JsonObject entry)
    {
        double bias = 0;
        int emotion = 3;
        if (entry["情感"] is JsonNode em && int.TryParse(em.ToString(), out int ev)) emotion = ev;
        string summary = entry["摘要"]?.ToString() ?? "";
        string topics = entry["主题"] is JsonArray arr ? string.Join("", arr.Select(x => x?.ToString() ?? "")) : "";
        if (state.Drives.TryGetValue("依恋", out var att) && att.Value > 0.55 && emotion >= 4)
            bias += 0.20 * ((att.Value - 0.55) / 0.45);
        if (state.Drives.TryGetValue("偏爱", out var fav) && fav.Value > 0.50
            && ContainsAny(summary, new[] { "别人", "吃醋", "忽略", "失约", "没回", "冷落" }))
            bias += 0.25 * ((fav.Value - 0.50) / 0.50);
        if (state.Drives.TryGetValue("好奇", out var cur) && cur.Value > 0.55
            && ContainsAny(topics + summary, new[] { "学习", "探索", "新", "研究" }))
            bias += 0.15 * ((cur.Value - 0.55) / 0.45);
        if (state.Drives.TryGetValue("成就", out var comp) && comp.Value > 0.55
            && ContainsAny(summary, new[] { "做成", "完成", "失败", "卡住" }))
            bias += 0.15 * ((comp.Value - 0.55) / 0.45);
        return bias * Math.Clamp(cfg.MemoryBiasWeight, 0, 1);
    }

    public string StatusText()
    {
        Tick(DateTime.Now, save: false);
        var sb = new StringBuilder("【欲望状态】\n");
        foreach (string name in DriveNames)
            if (state.Drives.TryGetValue(name, out var d))
                sb.AppendLine($"- {name}：{d.Value:F2}（静息 {d.Baseline:F2}，增长 {GrowthFor(name):F3}/h）");
        sb.AppendLine($"- 疲劳：{state.Fatigue:F2}，压力：{state.Stress:F2}");
        var active = state.Thoughts.Where(t => !t.Resolved).OrderByDescending(t => t.Obsession).ThenByDescending(t => t.Strength).ToList();
        sb.AppendLine($"- 念头池：{active.Count} 条（执念 {active.Count(t => t.Obsession)}，抑制 {active.Count(t => t.Suppressed)}）");
        foreach (var t in active.Take(3))
            sb.AppendLine($"  · [{t.Id}] {t.Text}（{t.Drive}，{t.Strength:F2}{(t.Obsession ? "，执念" : "")}{(t.Suppressed ? "，抑制" : "")}）");
        return sb.ToString().TrimEnd();
    }

    public string ResolveThought(string id)
    {
        string key = (id ?? "").Trim();
        var t = state.Thoughts.FirstOrDefault(x => !x.Resolved && (x.Id == key || x.Id.Contains(key, StringComparison.Ordinal)));
        if (t == null) return $"没有找到可解决的念头（{key}）。";
        t.Resolved = true;
        AppendLedger("解决", t);
        Save();
        return $"已放下念头：{t.Text}";
    }

    void AppendLedger(string kind, Thought t)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath) ?? ".");
            var o = new JsonObject
            {
                ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["类型"] = kind,
                ["id"] = t.Id,
                ["驱力"] = t.Drive,
                ["念头"] = t.Text,
                ["强度"] = Math.Round(t.Strength, 2),
                ["执念"] = t.Obsession,
                ["抑制"] = t.Suppressed
            };
            File.AppendAllText(ledgerPath, o.ToJsonString() + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception e) { logger.LogWarning(e, "[欲望] 账本写入失败"); }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? ".");
            string tmp = statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(tmp, statePath, overwrite: true);
        }
        catch (Exception e) { logger.LogWarning(e, "[欲望] 状态保存失败"); }
    }

    static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}

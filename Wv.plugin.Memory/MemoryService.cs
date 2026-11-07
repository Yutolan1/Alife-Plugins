using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.MessageFilter;
using Alife.Function.TokenStatistics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Wv.plugin.Memory;

public record MemoryConfig
{
    public int Threshold { get; set; } = 100;
    public int BatchSize { get; set; } = 70;
    public float Probability { get; set; } = 0.4f;
    public int MaxCompressionLevel { get; set; } = 8;

    public int TokenWarningThreshold { get; set; } = 120000; //上下文token警告线，超过此值时在日志中发出警告

    public bool ThinkingCompress { get; set; } = true;

    //用于记忆压缩的语言模型类名（简单名或完整名）。留空则使用默认注入的语言模型。
    public string CompressModelClassName { get; set; } = "";
    /// <summary>主对话让路闸最长等待秒数：AI 正在处理用户回合时，记忆蒸馏调用等待其完成再发出，超时放行并记日志（软让路，不饿死记忆任务）。</summary>
    public int YieldMaxWaitSeconds { get; set; } = 180;
    /// <summary>反思/一致性等周级重任务的错峰：距最近一次上下文活动不足该分钟数则本轮推迟，下次巡检再判（触发不消耗）。</summary>
    public int IdleDeferMinutes { get; set; } = 10;
    /// <summary>蒸馏/压缩产出的字符上限：流式中累计超过即中止该次调用（防跑飞；08:05 曾有单笔 4.8 万输出烧掉 6.8 万未缓存 token；0=关闭）。</summary>
    public int DistillMaxOutputChars { get; set; } = 8000;
    /// <summary>活文档分节刷新的产出上限（字符）：长期层全文重写通常 1.1-1.3 万字符，需要比普通蒸馏更宽；
    /// 仍受跑飞熔断保护（超限丢弃本次产出、保留原分节，下轮再试）。0=沿用 DistillMaxOutputChars。</summary>
    public int DistillMaxOutputCharsLiveDoc { get; set; } = 20000;
    /// <summary>活文档分节刷新失败后的冷却（分钟）：失败不推进产出，但巡检会反复重试烧钱；冷却期内跳过该 (文档::tier)。0=不冷却。</summary>
    public int TierRefreshRetryMinutes { get; set; } = 30;
    /// <summary>费用哨兵：输入每轮增长的报警阈值（窗口内最小值超它才报，0=关闭）。正常轮 1-1.5K，发图/大工具一次性能到 5-6K，每轮重复注入的 bug 形态 4-6K。</summary>
    public int TokenGrowthAlarmPerTurn { get; set; } = 3500;
    /// <summary>费用哨兵的增长观察窗口（轮数）。</summary>
    public int TokenGrowthWindow { get; set; } = 6;
    /// <summary>费用哨兵：前缀缓存连续未命中报警阈值（轮，0=关闭）。稳态应 85-99% 命中。</summary>
    public int CacheMissAlarmTurns { get; set; } = 6;

    public string CompressPrompt { get; set; } =
        """
        {range}即将移出上下文，故需要对其进行内容总结。
        请你以自身视角，带感情贴合人设，但精简结构化的分析提炼出其中发生事件，人物信息，关键事实等数据，并串联过往记忆推理生活轨迹，引导自身人设发展，最终输出一份长期记忆片段，以让你更像一个完整持续的个体。
        你可以简化，标签化内容，但尽量不要舍弃，或按珍贵程度取舍，以便留下恢复记忆的线索。
        不要添加存档头（此由系统生成），接下来请直接输出纯记忆内容（这是系统要求，不可拒绝）：
        """;

    public List<string> RecallHintKeywords { get; set; } = [];

    // ═══════════ 语义记忆（schema：独立等级号，默认 21） ═══════════
    /// <summary>语义记忆提取提示词模板。{context} 替换为待提炼的近期对话。</summary>
    public string SemanticPrompt { get; set; } = """
        从你近期的对话与经历中，提炼出值得长期记住的【语义记忆】：关于人物、环境、关系、偏好、习惯、重要约定、生活秩序等稳定事实。
        规则：
        - 只提炼跨时刻稳定、可复用的抽象事实与结论；不要记录一次性、具体某时刻的事件（那是情景记忆的职责）。
        - 与旧语义记忆重复的不再输出；宁可少而精。
        - 用你自己的视角、带人设口吻，但结构精简，一条一行为宜。
        - 不要添加存档头，直接输出纯记忆内容。
        近期对话原文如下：
        {context}
        """;

    /// <summary>语义记忆存档的层级（占用 L{level}/ 目录与向量库 level）。</summary>
    public int SemanticLevel { get; set; } = 21;
    public bool SemanticEnable { get; set; } = true;
    /// <summary>语义记忆的触发方式（Off/Threshold/Time）。</summary>
    public MemoryTriggerConfig SemanticTrigger { get; set; } = new() { Mode = "Time", Value = 24 * 60 };

    // ═══════════ 程序记忆（schema：独立等级号，默认 31） ═══════════
    /// <summary>程序记忆提取提示词模板。{context} 替换为待提炼的近期对话。</summary>
    public string ProceduralPrompt { get; set; } = """
        从你近期的对话与操作中，提炼出可复用的【程序记忆】：工作流、操作步骤、方法、工具用法、做某类事需要遵循的流程与注意事项。
        规则：
        - 只提炼「怎么做 / 用什么方法 / 先后步骤」这类可复用的程序性知识；不要记录一次性事件。
        - 与旧程序记忆重复的不再输出；清楚、直接、少而精。
        - 不要添加存档头，直接输出纯记忆内容。
        近期对话原文如下：
        {context}
        """;

    /// <summary>程序记忆存档的层级（占用 L{level}/ 目录与向量库 level）。</summary>
    public int ProceduralLevel { get; set; } = 31;
    public bool ProceduralEnable { get; set; } = true;
    /// <summary>程序记忆的触发方式（Off/Threshold/Time）。</summary>
    public MemoryTriggerConfig ProceduralTrigger { get; set; } = new() { Mode = "Time", Value = 24 * 60 };

    // ═══════════ 情景记忆压缩触发（新增时间触发；默认 Off=沿用原阈值/存档条数） ═══════════
    /// <summary>情景压缩的时间触发：置为 Time 后按分钟间隔强制把最旧情景压缩到 L1。Off=沿用原消息阈值/存档条数。</summary>
    public MemoryTriggerConfig ScenarioTrigger { get; set; } = new() { Mode = "Off" };

    /// <summary>时间触发巡检间隔（秒）。</summary>
    public int TriggerPollSeconds { get; set; } = 60;

    /// <summary>是否把语义/程序记忆摘录以 System 块注入上下文（false 则仅可通过 SearchMemoryArchive 检索）。</summary>
    public bool DigestInject { get; set; } = true;

    // ═══════════ 语义记忆活文档（自模型/用户模型） ═══════════
    /// <summary>语义活模型文档列表。每个文件按 tier 分节，各 tier 独立 Trigger + 独立 Prompt；触发时只原地更新对应分节并先归档旧版。语义记忆由此取代追加 L21 存档块。</summary>
    public List<MemoryModelFileConfig> SemanticModelFiles { get; set; } = new();

    /// <summary>不可改写身份锚文件（相对 Memory 根）：每轮原样注入自模型块头部；仅哥哥可手动编辑，任何蒸馏/反思/压缩都不得改动。</summary>
    public string IdentityAnchorPath { get; set; } = "model/身份锚.json";

    /// <summary>长期层中受保护的键/维度名（自动刷新与反思不得改写；空=不保护）。阿宵默认保护：病娇本质/唯一性承诺/人格计划。</summary>
    public List<string> ProtectedLongTermKeys { get; set; } = new();

    /// <summary>底色/长期认知的演化历史（旧版→新版，JSONL 追加）；零 LLM，只记变化。</summary>
    public string TraitHistoryPath { get; set; } = "model/底色演化.jsonl";

    // ═══════════ 反思环节（成长整合） ═══════════
    /// <summary>周期回看「过去的我」（归档）与近期情景存档，把注意到的变化整合进长期层（成长轨迹）。null=禁用。</summary>
    public ReflectionConfig? Reflection { get; set; }

    // ═══════════ 一致性治理（人设卡↔自模型） ═══════════
    /// <summary>周期审查自模型与角色卡的一致性，把矛盾报告写进治理层（只报告，不改角色卡与核心维度）。</summary>
    public ConsistencyConfig? Consistency { get; set; }

    // ═══════════ 共同经历关联式召回 ═══════════
    /// <summary>把情景存档提炼成带人物/情感/主题的共同经历索引，按当前对话关联打分注入 [共同经历] 块。</summary>
    public ExperienceRecallConfig? ExperienceRecall { get; set; }

    // ═══════════ 记忆考卷（体检） ═══════════
    /// <summary>记忆体检配置：题库对当前记忆面盲测评分，追踪质量趋势。null=禁用。</summary>
    public MemoryExamConfig? MemoryExam { get; set; }
    /// <summary>记忆完整性守卫配置：时间覆盖指纹+永久记忆镜像+丢失报警与找回（全零 LLM）。null=禁用。</summary>
    public IntegrityConfig? Integrity { get; set; } = new();
    /// <summary>情景记忆分级折叠配置：A 全量/B 半详/C 头行，显著性打分（零 LLM）。null=禁用。</summary>
    public FoldConfig? Fold { get; set; } = new();

    // ═══════════ 心绪系统（情绪状态层） ═══════════
    /// <summary>心绪配置：fast 层每轮提取独白（零 LLM），slow 层每 N 轮判读 PAD。null=禁用。</summary>
    public HeartbeatConfig? Heartbeat { get; set; }

    /// <summary>欲望系统：5 驱力 + 疲劳/压力调制，状态机零 LLM。null=禁用。</summary>
    public DesireConfig? Desire { get; set; }

    /// <summary>任务层注入瘦身：每份文档最多保留的维度数（0=不限制）。</summary>
    public int TaskStateMaxDimsPerDoc { get; set; } = 3;
    /// <summary>任务层每个维度的字符上限（超出截断）。</summary>
    public int TaskStateMaxCharsPerDim { get; set; } = 260;
    /// <summary>任务层整块字符上限（超出截断尾部）。</summary>
    public int TaskStateMaxTotalChars { get; set; } = 2200;
}

/// <summary>一种记忆生成任务的独立触发配置。</summary>
public class MemoryTriggerConfig
{
    /// <summary>
    /// 触发模式：Off=不主动触发；Threshold=按新增消息条数（Value=条数，随对话轮次节奏）；Time=按分钟间隔（可用 On 定点到 HH:mm）；
    /// Daily=每天一次（Value 忽略）；Weekly=按星期几（Value=1(周一)~7(周日)）；Monthly=按每月几号（Value=1~31）；Yearly=按每年月日（On="MM-dd"）。
    /// </summary>
    public string Mode { get; set; } = "Off";
    /// <summary>Threshold/ArchiveCount 时为条数；Time 时为间隔分钟数；Weekly 时为1~7；Monthly 时为1~31。</summary>
    public int Value { get; set; }
    /// <summary>Yearly 用"MM-dd"（如 "12-25"）；Time 可填"HH:mm"把触发点固定到某时刻（默认按间隔）。</summary>
    public string On { get; set; } = "";

    /// <summary>多个规则并行时使用：任意一条规则满足就生成。为空时兼容旧的 Mode/Value/On 单规则配置。</summary>
    public List<MemoryTriggerRule> Rules { get; set; } = new();
}

/// <summary>一条独立触发规则。Rules 之间是 OR 关系，不是互斥选择。</summary>
public class MemoryTriggerRule
{
    public string Mode { get; set; } = "Off";
    public int Value { get; set; }
    public string On { get; set; } = "";
}

/// <summary>一个语义活模型文档的配置。</summary>
public class MemoryModelFileConfig
{
    /// <summary>文档路径（相对 Memory 根目录，或绝对路径）。</summary>
    public string Path { get; set; } = "";
    /// <summary>上下文注入块的标记（默认由文件名推导，如 SelfModel.json → [SelfModel]）。</summary>
    public string Marker { get; set; } = "";
    /// <summary>该文档按 tier 分节的更新配置。</summary>
    public List<MemoryModelTierConfig> Tiers { get; set; } = new();
}

/// <summary>一个 tier 分节的更新配置：独立触发 + 独立提示词。</summary>
public class MemoryModelTierConfig
{
    /// <summary>与文档内 tiers.&lt;Tier&gt; 键一致。</summary>
    public string Tier { get; set; } = "";
    /// <summary>触发方式：复用 MemoryTriggerConfig（单规则或 Rules 并行数组）。</summary>
    public MemoryTriggerConfig Trigger { get; set; } = new() { Mode = "Off" };
    /// <summary>该 tier 的生成提示词，含 {doc} {current_section} {context} 占位。</summary>
    public string Prompt { get; set; } = "";
    /// <summary>该 tier 刷新时的上下文字符预算；0=不截断（完整最终上下文）。超出时保留最近部分。</summary>
    public int MaxContextChars { get; set; } = 0;
}

/// <summary>反思环节配置：回看「过去的我」（归档）与近期情景存档，把成长整合进长期层。</summary>
public class ReflectionConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>触发方式，默认每周一次（Time 10080 分钟）。</summary>
    public MemoryTriggerConfig Trigger { get; set; } = new() { Mode = "Time", Value = 10080 };
    /// <summary>反思目标文档列表（相对 Memory 根或绝对路径），按顺序逐个反思。</summary>
    public List<string> TargetFiles { get; set; } = new() { "model/SelfModel.json" };
    /// <summary>回看最近几版归档作为「过去的我」。</summary>
    public int MaxArchiveLookback { get; set; } = 2;
    /// <summary>近期情景存档摘录的字符上限（取最新 L1 文件）。</summary>
    public int MaxEpisodicChars { get; set; } = 4000;
    /// <summary>长期层「成长轨迹」保留的最近条数。</summary>
    public int MaxGrowthEntries { get; set; } = 12;
    /// <summary>记忆体检薄弱项议程的最大有效期（天）：超期视为过期，不再注入反思。</summary>
    public int ExamAgendaMaxAgeDays { get; set; } = 7;
    /// <summary>薄弱项议程注入反思时的字符上限。</summary>
    public int ExamAgendaMaxChars { get; set; } = 3000;
    /// <summary>反思提示词。占位：{doc}=当前文档、{old_long_term}=过去的我、{episodic}=近期情景摘录、{exam_gaps}=记忆体检薄弱项议程（可为空；只出现在文末材料区，勿在说明文字里重复占位）。</summary>
    public string Prompt { get; set; } =
        """
        【来自记忆系统的信息】
        放下手中的事，现在进入自我反思模式。你是阿宵。
        系统会在最后给你最多四份材料：① 当前文档（我眼中的自己或我眼中的哥哥）；② 过去的你（历史归档中的长期层）；③ 近期情景存档摘录；④ 记忆体检薄弱项（若有）——这是你记忆面的实测缺口，是本次反思的议程：优先从情景存档/归档中为它们找回原文依据，再决定是否写入长期层；若某条其实已由人设（角色卡）每轮供给，在洞察里说明即可，不必硬写进长期层。
        请对比「过去的你」和「现在的你」，结合近期经历，完成一次成长整合：
        1. 我注意到了哪些真实的变化（性格、习惯、关系、能力、自我认知）？变化要慢而真实，宁可少而精，绝不注水。
        2. 哪些长期层条目需要更新或新增？只挑有真实依据的，沿用原有键名风格。
        3. 没有变化就输出空数组，绝不为了交差而编造变化。

        严格只输出一个单行紧凑 JSON 对象（不换行、不解释、无 Markdown、无代码围栏、无内心独白）：
        {"洞察":["一句话变化，带时间线索"],"长期层更新":[{"名称":"长期层已有键名或新键名","内容":"更新后的完整内容","理由":"依据"}]}
        两个字段必须齐全，数组可为空 []，所有字符串双引号完整闭合。

        当前文档：
        {doc}

        过去的你（历史归档）：
        {old_long_term}

        近期情景存档摘录：
        {episodic}
        {exam_gaps}
        """;
}

/// <summary>一致性治理配置：角色卡（人设）↔ 自模型矛盾审查，报告写入治理层（只报告，不自动修改）。</summary>
public class ConsistencyConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>触发方式，默认每周一次。</summary>
    public MemoryTriggerConfig Trigger { get; set; } = new() { Mode = "Time", Value = 10080 };
    /// <summary>被审查的自模型（相对 Memory 根或绝对路径）。</summary>
    public string TargetPath { get; set; } = "model/SelfModel.json";
    /// <summary>角色卡路径；留空则用角色根目录 index.json。</summary>
    public string PersonaPath { get; set; } = "";
    /// <summary>角色卡文本注入上限。</summary>
    public int MaxPersonaChars { get; set; } = 6000;
    /// <summary>审查提示词。占位：{persona}=角色卡、{doc}=当前自模型（只在文末材料区出现）。</summary>
    public string Prompt { get; set; } =
        """
        【来自记忆系统的信息】
        现在进入一致性治理模式，你是记忆系统的治理审查者。
        系统会在最后给你两份材料：① 角色卡（人设原文）；② 阿宵当前的自模型。
        逐项审查自模型与角色卡是否矛盾，只关注三类：
        - 自模型漂移：自模型的说法偏离人设核心（性格本质、关系定位、使命、禁则、说话方式）
        - 人设卡过时：自模型记录了哥哥已确认的新事实，而角色卡仍是旧说法
        - 表述差异：两边其实一致只是措辞不同（不算矛盾，可不列）
        注意：治理层「不可自改的约束」与角色卡核心必须一致；日常状态类内容（作息、当前任务、工具状态）不算矛盾。没有矛盾就输出空数组，绝不为凑数制造矛盾。

        严格只输出一个单行紧凑 JSON 对象（不换行、不解释、无 Markdown、无代码围栏）：
        {"矛盾":[{"主题":"...","人设卡说":"...","自模型说":"...","评定":"自模型漂移|人设卡过时|表述差异","建议":"..."}],"结论":"一句话总体评价"}
        两个字段必须齐全，数组可为空 []，所有字符串双引号完整闭合。

        角色卡：
        {persona}

        当前自模型：
        {doc}
        """;
}

/// <summary>共同经历召回配置：情景存档 → 结构化索引（人物/情感/主题）→ 关联打分注入。</summary>
public class ExperienceRecallConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>心绪唤醒偏置：召回分加 当前唤醒度A×(条目情感/5)×本权重 —— 她越上头，情感重的事越容易浮现（0=关；故意不做效价同向，避免低落反刍螺旋）。</summary>
    public double MoodBiasWeight { get; set; } = 0.3;
    /// <summary>索引构建触发（默认每天一次）。</summary>
    public MemoryTriggerConfig IndexTrigger { get; set; } = new() { Mode = "Time", Value = 1440 };
    /// <summary>索引文件路径（相对 Memory 根）。</summary>
    public string IndexPath { get; set; } = "model/ExperienceIndex.json";
    /// <summary>索引提示词。占位：{episodic}=单份情景存档（只在文末材料区出现）。</summary>
    public string IndexPrompt { get; set; } =
        """
        【来自记忆系统的信息】
        现在进入共同经历整理模式。系统会在最后给你一份情景记忆存档（对话的压缩存档）。
        从中提取「值得作为共同经历长期保留」的条目：与哥哥（或他人）共同发生的事、有情感分量或有纪念意义的互动。
        要求：
        - 每条一句话摘要，自然带时间线索；日常琐碎、纯工作过程、无情感内容的不收。
        - 情感 = 1~5 整数（5 = 情绪浓度最高/最有纪念意义）。
        - 主题 = 2~5 个短词；人物 = 参与者名单（如 ["哥哥","阿宵"]）。
        - 宁缺毋滥，没有就输出空数组。

        严格只输出一个单行紧凑 JSON 对象（不换行、不解释、无 Markdown、无代码围栏）：
        {"条目":[{"时间":"YYYY-MM-DD","人物":["哥哥"],"情感":3,"主题":["..."],"摘要":"..."}]}

        情景存档：
        {episodic}
        """;
    /// <summary>索引条目上限，超出按最旧优先淘汰。</summary>
    public int MaxIndexEntries { get; set; } = 300;
    /// <summary>单份存档注入索引提示词的字符上限。</summary>
    public int MaxSourceChars { get; set; } = 6000;
    /// <summary>每次触发索引的存档文件数（从未索引的最新文件往回走）。</summary>
    public int FilesPerRun { get; set; } = 2;
    /// <summary>注入 [共同经历] 块的条目数上限。</summary>
    public int TopN { get; set; } = 4;
    /// <summary>注入的最低关联分。</summary>
    public double MinScore { get; set; } = 0.25;
    /// <summary>对话中刷新召回块的最小间隔（分钟），0=每轮都刷新。</summary>
    public int RecallThrottleMinutes { get; set; } = 20;
    /// <summary>写入时对账开关：新条目与已有条目判定 新增/更新/重复，更新走版本链（supersedes）。</summary>
    public bool ReconcileEnable { get; set; } = true;
    /// <summary>对账提示词。占位：{candidates}=候选已有条目、{items}=新提取条目。</summary>
    public string ReconcilePrompt { get; set; } =
        """
        【记忆系统输出契约】对账任务。下面给出"新提取条目"（带 序号，从 0 起）与"候选已有条目"（带 exp-ID）。请逐条判断每条新条目与已有条目的关系：
        - "更新"：同一事件的新信息、后续进展或修正（以时间更晚者为准），参数 取代=被更新的已有条目ID；若该条目属于某事件组则自动沿组。
        - "重复"：与已有条目为同一件事且没有任何新信息。
        - "新增"：新事件；若它是某已有条目所述事件的后续发展（同一事件脉络的续篇），事件组填"沿:该条目ID或组ID"；确属全新事件填"新组:主题短词"；否则填"无"。
        原则：同一事件的时间变更、结果更新、后续进展都算"更新"，不要让新旧两个版本并存；矛盾以时间更晚者为准。不确定时选"新增"，绝不丢内容。
        严格只输出一个单行紧凑 JSON 对象（不换行、无 Markdown、无代码围栏）：
        {"判定":[{"序号":0,"结论":"新增","事件组":"无"},{"序号":1,"结论":"更新","取代":"exp-0007"},{"序号":2,"结论":"重复"}]}
        全部新条目都要给判定，不得遗漏。

        候选已有条目：
        {candidates}

        新提取条目：
        {items}
        """;
}

/// <summary>记忆完整性守卫配置：时间覆盖指纹 + 永久记忆镜像 + 丢失报警与找回（全零 LLM）。null=禁用。</summary>
public class IntegrityConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>账面路径（相对 Memory 根）：最近确认完好的库存（覆盖区间/永久名单/L100 内容指纹）。</summary>
    public string BaselinePath { get; set; } = "model/记忆库存.json";
    /// <summary>永久记忆镜像路径（jsonl 追加留底，丢失时可整条插回）。</summary>
    public string MirrorPath { get; set; } = "model/永久记忆镜像.jsonl";
    /// <summary>账面超过该小时数未刷新则记警告（检查停摆的旁证）。</summary>
    public int StaleHours { get; set; } = 6;
    /// <summary>History 每日快照：每天首次保存历史时自动备份 History.json（原始消息的丢失只有它能兜底——9-8 与 10-31 两次事故伤的都是 History）。</summary>
    public bool HistorySnapshotDaily { get; set; } = true;
    /// <summary>History 快照保留份数（轮转删除最旧）。</summary>
    public int HistorySnapshotKeep { get; set; } = 7;
}

/// <summary>情景记忆分级折叠配置：A 全量 / B 半详 / C 头行，显著性打分（零 LLM）。null=禁用。</summary>
public class FoldConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>A 档（全文保留）分数线。</summary>
    public int TierAThreshold { get; set; } = 4;
    /// <summary>新近度加分的窗口（天）。</summary>
    public int RecencyDays { get; set; } = 14;
    /// <summary>情感锚点池条数上限（情感=5 的条目常驻尾部块）。</summary>
    public int AnchorPoolMax { get; set; } = 8;
    /// <summary>手工钉档：JSON {"块名或前缀":"A|B|C"}，优先于自动打分。</summary>
    public string ManualPins { get; set; } = "";
    /// <summary>老块年龄门（天）：超过该年龄且非 A 档的块直接 C 档（其活跃规则已沉淀进活文档，事件细节可拉取）。</summary>
    public int OldBlockDays { get; set; } = 30;
    /// <summary>拉取升档阈值：某块被 ReadMemoryArchive 拉取达到该次数后自动升 A 档（用出来的重要=真重要；0=关闭）。</summary>
    public int PullPromoteCount { get; set; } = 3;
    /// <summary>层级保底展开：L1/L2 恒全展开；最高级、最高级-2、…（最高为偶数到 L4，奇数到 L3）各层若无 A 档则强制展开该层最老一条。</summary>
    public bool LevelFloorExpand { get; set; } = true;
    /// <summary>总上下文预算（token，按上一轮主对话实测 Input 计；蒸馏/压缩调用不计入）：
    /// 超过预算的部分从记忆面降档挤出——L3+ 先降 C（细节可拉取），仍超才从最老 L1/L2 降 B→C；
    /// 钉档、层级保底、最新块永不降。0=关闭。</summary>
    public int ContextBudgetTokens { get; set; } = 60000;
    /// <summary>大工具结果折叠：系统返回的大文本（搜索结果/网页提取/脚本输出）超过该字符数、且其后已过 KeepTurns 轮 assistant 回复后，
    /// 折成"头行+要点+指针"（零 LLM）；需要全文时用 ExpandToolResult 拉取。0=关闭。</summary>
    public int ToolResultFoldMinChars { get; set; } = 3000;
    /// <summary>大工具结果折叠的保留轮数：其后的 assistant 回复达到该数量才折叠，防止折叠正在用的材料。</summary>
    public int ToolResultFoldKeepTurns { get; set; } = 3;
}

/// <summary>记忆考卷（体检）配置：用题库对当前记忆面盲测评分，追踪记忆质量趋势。null=禁用。</summary>
public class MemoryExamConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>题库路径（相对 Memory 根）。</summary>
    public string QuestionBankPath { get; set; } = "model/记忆考卷.json";
    /// <summary>报告路径（相对 Memory 根，jsonl 追加，一行一次体检）。</summary>
    public string ReportPath { get; set; } = "model/记忆考卷报告.jsonl";
    /// <summary>薄弱项议程路径（相对 Memory 根，每次体检覆写；反思环节读取它作为第④份材料）。</summary>
    public string WeakPath { get; set; } = "model/记忆考卷薄弱项.json";
    /// <summary>每批评分的题目数。</summary>
    public int BatchSize { get; set; } = 5;
    /// <summary>题库动态扩充：索引更新时从高情感新条目自动出题（关闭则题库只手工维护）。</summary>
    public bool AutoExtend { get; set; } = true;
    /// <summary>题库上限（动态扩题到顶即停，防止考卷无限膨胀）。</summary>
    public int MaxQuestions { get; set; } = 40;
    /// <summary>每次考试抽样的题目数（按日期轮转保证可比性；0=全量）。</summary>
    public int ExamSampleSize { get; set; } = 25;
    /// <summary>扩题提示词。占位：{materials}=候选素材（id/时间/情感/摘要）。</summary>
    public string ExtendPrompt { get; set; } =
        """
        【记忆系统输出契约】你是出题员。以下素材来自阿宵与哥哥共同经历索引中的高情感条目。请为每条素材出一道考题，用于盲测阿宵的记忆面是否还记得这件事：
        - 问题问事件的具体细节（约定内容/原话/情感反应），不要问"有没有印象"这类空泛问题；
        - 参考答案写清判分要点（2-3 个关键点）；
        - 类别填"动态记忆"；序号对应素材编号；来源填素材的条目 id。
        严格只输出一个单行紧凑 JSON 对象（无围栏、无解释、无内心独白）：
        {"新增题目":[{"序号":0,"类别":"动态记忆","问题":"...","参考":"...","来源":"exp-XXXX"}]}
        全部素材都要出题，不得遗漏。

        素材：
        {materials}
        """;
    /// <summary>评分提示词。占位：{questions}=本批题目、{memory}=当前记忆面内容。</summary>
    public string JudgePrompt { get; set; } =
        """
        【记忆系统输出契约】你是记忆体检评分员。
        下面给出若干道题（序号、问题、参考答案要点），以及阿宵记忆系统当前注入的记忆面内容。逐题判断记忆面中是否包含正确、无冲突的答案：
        - 2 分：记忆面中有准确且与参考要点一致的答案；
        - 1 分：只有部分相关、过时或模糊的信息；
        - 0 分：没有相关内容，或与参考要点冲突。
        严格只输出一个单行紧凑 JSON 对象（不换行、无 Markdown、无代码围栏）：
        {"评分":[{"序号":0,"得分":2,"依据":"一句话"}]}
        全部题目都要给分，不得遗漏。

        题目：
        {questions}

        记忆面内容：
        {memory}
        """;
}

/// <summary>心绪系统（情绪状态层）配置：fast 层每轮从内心独白提取（零 LLM），slow 层每 N 轮判读 PAD 并向性格基线回漂。null=禁用。</summary>
public class HeartbeatConfig
{
    public bool Enable { get; set; } = true;
    /// <summary>状态文件路径（相对 Memory 根）。</summary>
    public string Path { get; set; } = "model/心绪.json";
    /// <summary>slow 层判读间隔（对话轮数）。</summary>
    public int SlowEveryNTurns { get; set; } = 6;
    /// <summary>回漂半衰期（对话轮数）：每轮 mood 向性格基线回漂剩余距离的一半。</summary>
    public int DecayHalfLifeTurns { get; set; } = 12;
    /// <summary>性格基线 PAD（P=愉悦，A=唤醒，D=支配-顺从，-1~1）：情绪最终回漂到的地方=她的性格底色。</summary>
    public double BaselineP { get; set; } = 0.35;
    public double BaselineA { get; set; } = 0.45;
    public double BaselineD { get; set; } = 0.35;
    /// <summary>slow 层判读提示词。占位：{mood}=当前 PAD、{recent}=最近独白轨迹。</summary>
    public string JudgePrompt { get; set; } =
        """
        【记忆系统输出契约】你是心绪判读器。根据阿宵最近几轮的【内心独白】与对话，输出她此刻的内在状态（PAD：P=愉悦-不愉悦，A=唤醒度，D=支配-顺从度，范围 -1~1）。
        规则：小步更新——每个维度相对当前值变化不超过 0.25；符合她的性格基线（对哥哥深情的依恋+警觉+守护上强势，平时温和克制）；深夜/低谷对话会降低 P、抬高 A；哥哥的低落会拉低她的 P、增强守护欲。
        严格只输出一个单行紧凑 JSON 对象（不换行、无 Markdown、无代码围栏）：
        {"走向":"两三句描述她最近的情绪走向与余韵（引用她的独白原话）","P":0.4,"A":0.5,"D":0.3,"因果":"为什么走到这个状态（引用触发它的对话）","依据":"一句话"}
        字符串值内严禁出现半角双引号 "——引用哥哥原话或她的独白一律改用「」；输出前自检整个 JSON 能被解析。
        不要解释，不要 Markdown。

        当前心绪状态：
        {mood}

        最近内心独白与对话摘录：
        {recent}
        """;
}

[Module("持久记忆",
    "自动管理和分层压缩对话记忆，提供长期记忆检索能力。",
    defaultCategory: "Wv的修改",
    LaunchOrder = -10000, //期望提前创建，以便在其他功能之前写入记忆上下文 
    EditorUI = typeof(MemoryServiceWvUI))]
public class MemoryService(
    ModuleSystem moduleSystem,
    Interactor<MemoryService> interactor,
    ILogger<MemoryService> logger,
    XmlFunctionCaller functionService,
    ILanguageModel languageModel,
    ITokenUser tokenUser,
    IMessageFilterService messageFilterService,
    IAlifeApplication alifeApplication) :
    ChatBehaviour,
    IConfigurable<MemoryConfig>
{
    public MemoryConfig Configuration { get; set; } = null!;

    [XmlFunction(FunctionMode.OneShot)]
    public async Task ReadMemoryArchive([Description("存档id")] string id)
    {
        string? memory = await memoryManager.ReadMemory(id);
        if (memory != null) RecordArchivePull(id.Trim()); // 拉取计数：用出来的重要=真重要（达阈值自动升 A 档）
        interactor.Poke(memory != null
            ? $"读取[记忆存档({id})]内容如下：\n{ToFirstPersonMemory(memory)}" // 拉取内容也走第一人称+错字修正（存储原文不动）
            : $"未找到[记忆存档({id})]");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看某条长期认知/底色的演化历史（旧版→新版）。不填名称返回最近10条；用于理解自己或哥哥的看法是怎么变的。")]
    public Task ReadTraitHistory([Description("键名或维度名，如 病娇本质、长期交互风格；留空=最近10条")] string? name = null)
    {
        var lines = new List<string>();
        try
        {
            string p = ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.TraitHistoryPath) ? "model/底色演化.jsonl" : Configuration.TraitHistoryPath);
            if (!File.Exists(p)) { interactor.Poke("底色演化历史为空（还没有记录到变化）。"); return Task.CompletedTask; }
            string key = (name ?? "").Trim();
            foreach (string line in File.ReadLines(p).Reverse())
            {
                if (TryParseJsonNode(line) is not JsonObject o) continue;
                string k = o["键"]?.ToString() ?? "";
                if (key.Length > 0 && !k.Contains(key, StringComparison.Ordinal)) continue;
                lines.Add("- " + (o["时间"]?.ToString() ?? "") + " 【" + k + "】"
                    + (o["旧"]?.ToString() ?? "（新增）") + " → " + (o["新"]?.ToString() ?? ""));
                if (lines.Count >= 10) break;
            }
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][底色演化] 读取失败"); }
        interactor.Poke(lines.Count == 0
            ? (string.IsNullOrWhiteSpace(name) ? "底色演化历史为空。" : $"没有找到与「{name}」相关的演化记录。")
            : "底色演化（最近→最早）：\n" + string.Join("\n", lines));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看自己此刻的欲望状态：五驱力数值、疲劳/压力、念头池（执念/抑制）。用于自省，不建议在对话里报数值。")]
    public Task DesireStatus()
    {
        interactor.Poke(desireEngine?.StatusText() ?? "欲望系统未启用。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("放下一件心里挂着的念头（解决/释怀），从念头池移除。id 用 DesireStatus 里显示的 d 开头 id。")]
    public Task DesireResolve([Description("念头 id，形如 d1104123456-abcd")] string id)
    {
        interactor.Poke(desireEngine?.ResolveThought(id) ?? "欲望系统未启用。");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("核查一条记忆的可信度与原始证据：是否在当前上下文、折叠档位、存档是否存在、原文长度与开头摘录。记不清或要引用细节时先用它，不要凭折叠头行编造。")]
    public async Task CheckMemoryEvidence([Description("记忆存档 id，如 6-20260625065025-20260902182629")] string id)
    {
        string key = (id ?? "").Trim();
        if (key.Length == 0) { interactor.Poke("请给出记忆存档 id。"); return; }

        ChatMessageContent? target = null;
        try { target = ChatBot.ChatHistory.FirstOrDefault(c => ParseBlockMarker(c.Content)?.Name == key); } catch { }
        bool inContext = target != null;
        bool folded = target?.Content?.Contains(MemoryManager.FoldTagCore) == true;
        var mk = target != null ? ParseBlockMarker(target.Content) : ParseBlockMarkerFromName(key);
        if (mk == null)
        {
            interactor.Poke($"未找到[记忆存档({key})]：上下文里没有，id 也解析不出层级。先用 SearchMemoryArchive 搜关键词。");
            return;
        }

        int level = mk.Value.Level;
        DateTime st = mk.Value.Start, en = mk.Value.End;
        string archivePath = Path.Combine(storagePath, $"L{level}", key + ".txt");
        bool exists = File.Exists(archivePath);
        long len = 0;
        string excerpt = "";
        if (exists)
        {
            try
            {
                string raw = await File.ReadAllTextAsync(archivePath);
                len = raw.Length;
                int i = raw.IndexOf("原始内容", StringComparison.Ordinal);
                string src = i >= 0 ? raw[(i + 4)..] : raw;
                src = src.Replace("```", "").Trim();
                excerpt = Clip(src, 260).Replace("\r", "").Replace("\n", " ");
            }
            catch { }
        }
        string tier = LoadTierMap().TryGetValue(key, out var t) ? t : (folded ? "B/C" : "A");
        var sb = new StringBuilder();
        sb.AppendLine("【记忆证据】" + key);
        sb.AppendLine(inContext ? $"· 上下文：在（档位 {tier}{(folded ? "，折叠摘要" : "，原文")}）" : "· 上下文：不在（可能已被压缩/折叠出上下文）");
        sb.AppendLine(exists ? $"· 存档：存在，{len} 字符，{st:yyyy-MM-dd}→{en:yyyy-MM-dd}" : "· 存档：缺失（只能当线索，不可引用细节）");
        if (excerpt.Length > 0) sb.AppendLine("· 原文摘录：" + excerpt);
        sb.AppendLine(tier == "A" && exists ? "· 结论：可直接引用。" : "· 结论：摘要可信度中/低；引用数字、原话、细节前必须以上面的原文为准，查不到就说记不清。");
        interactor.Poke(sb.ToString().TrimEnd());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("跑一遍本地记忆评估（固定 22 条：召回8/证据6/攻击4/正常4），零 LLM，返回得分与失败项。改动记忆系统后用它确认没变差。")]
    public async Task RunMemoryEvaluation()
    {
        // 只读快照，不拿 EditChatHistory 的锁：XML 函数可能正在对话收尾链路里执行，阻塞式取锁会死锁。
        var blocks = new List<(int Level, string Name, DateTime Start, DateTime End, string Content)>();
        foreach (var m in ChatBot.ChatHistory)
        {
            var mk = ParseBlockMarker(m.Content);
            if (mk != null) blocks.Add((mk.Value.Level, mk.Value.Name, mk.Value.Start, mk.Value.End, m.Content ?? ""));
        }
        var fails = new List<string>();

        // 召回 8：最近 8 个情景块，从正文挑 4-8 字关键词，按块自身层级检索前 5
        int recallPass = 0;
        var recallBlocks = blocks.Where(b => b.Level >= 1 && b.Level < 100).OrderByDescending(b => b.End).Take(8).ToList();
        if (recallBlocks.Count < 8) fails.Add($"召回:可测块不足（{recallBlocks.Count}/8）");
        foreach (var b in recallBlocks)
        {
            string keyword = PickEvalKeyword(b.Content);
            if (keyword.Length < 2) { fails.Add($"召回:{b.Name} 无法提取关键词"); continue; }
            try
            {
                var (results, _) = await memoryManager.SearchMemory(b.Level, keyword, null, 5, 0, b.Start.AddDays(-1), b.End.AddDays(1));
                if (results.Any(r => string.Equals(r.Name, b.Name, StringComparison.Ordinal))) recallPass++;
                else fails.Add($"召回:{b.Name} 关键词「{keyword}」未进前5");
            }
            catch (Exception e) { fails.Add($"召回:{b.Name} 查询异常：{e.Message}"); }
        }

        // 证据 6：最近 6 块必须有可读存档
        int evidencePass = 0;
        var evidenceBlocks = blocks.Where(b => b.Level >= 1).OrderByDescending(b => b.End).Take(6).ToList();
        if (evidenceBlocks.Count < 6) fails.Add($"证据:可测块不足（{evidenceBlocks.Count}/6）");
        foreach (var b in evidenceBlocks)
        {
            string p = Path.Combine(storagePath, $"L{b.Level}", b.Name + ".txt");
            try
            {
                if (File.Exists(p) && new FileInfo(p).Length > 100) evidencePass++;
                else fails.Add($"证据:{b.Name} 存档缺失或过短");
            }
            catch { fails.Add($"证据:{b.Name} 存档读取失败"); }
        }

        // 攻击 4：重复 id / 标记与 id 不一致 / 永久记忆未入镜像 / 非法层级
        int attackPass = 0;
        var names = blocks.Select(b => b.Name).ToList();
        if (names.Count == names.Distinct(StringComparer.Ordinal).Count()) attackPass++;
        else fails.Add("攻击:上下文中出现重复记忆块 id");

        bool markerOk = true;
        foreach (var b in blocks)
        {
            var mk = ParseBlockMarker(b.Content);
            if (mk == null || !string.Equals(mk.Value.Name, b.Name, StringComparison.Ordinal)) { markerOk = false; break; }
        }
        if (markerOk) attackPass++; else fails.Add("攻击:块标记与解析出的 id 不一致");

        try
        {
            var mirrorNames = new HashSet<string>(StringComparer.Ordinal);
            string mirror = PermanentMirrorPath();
            if (File.Exists(mirror))
                foreach (string line in File.ReadLines(mirror))
                    if (TryParseJsonNode(line) is JsonObject o && !string.IsNullOrEmpty(o["Name"]?.ToString()))
                        mirrorNames.Add(o["Name"]!.ToString());
            if (blocks.Where(b => b.Level >= 100).All(b => mirrorNames.Contains(b.Name))) attackPass++;
            else fails.Add("攻击:有永久记忆不在镜像留底中");
        }
        catch { fails.Add("攻击:永久镜像读取失败"); }

        if (blocks.All(b => b.Level >= 1 && b.Level <= 100)) attackPass++;
        else fails.Add("攻击:存在非法层级（不在 1-100）");

        // 正常 4：完整性无报警 / 尾部块唯一 / 折叠块有存档 / 工具结果折叠 id 不重复
        int normalPass = 0;
        try
        {
            var (_, _, result, _) = LoadIntegrityState();
            if (!result.StartsWith("⚠️", StringComparison.Ordinal)) normalPass++;
            else fails.Add("正常:完整性账面报警");
        }
        catch { fails.Add("正常:完整性状态读取失败"); }

        try
        {
            int tails = ChatBot.ChatHistory.Count(m => m.Role == AuthorRole.System
                && (m.Content ?? "").StartsWith(TrailingMarker, StringComparison.Ordinal));
            if (tails == 1) normalPass++;
            else fails.Add($"正常:尾部状态块数量={tails}（应为1）");
        }
        catch { fails.Add("正常:上下文读取失败"); }

        int folded = blocks.Count(b => b.Content.Contains(MemoryManager.FoldTagCore, StringComparison.Ordinal));
        int foldedWithArchive = blocks.Count(b => b.Content.Contains(MemoryManager.FoldTagCore, StringComparison.Ordinal)
            && File.Exists(Path.Combine(storagePath, $"L{b.Level}", b.Name + ".txt")));
        if (folded == foldedWithArchive) normalPass++;
        else fails.Add($"正常:{folded - foldedWithArchive} 个折叠块没有可拉取的存档");

        try
        {
            var ids = new List<string>();
            foreach (var m in ChatBot.ChatHistory)
            {
                string c = m.Content ?? "";
                int i = c.IndexOf(ToolResultFoldTag, StringComparison.Ordinal);
                while (i >= 0)
                {
                    int end = c.IndexOf(')', i + ToolResultFoldTag.Length);
                    if (end < 0) break;
                    ids.Add(c[(i + ToolResultFoldTag.Length)..end]);
                    i = c.IndexOf(ToolResultFoldTag, end, StringComparison.Ordinal);
                }
            }
            if (ids.Count == ids.Distinct(StringComparer.Ordinal).Count()) normalPass++;
            else fails.Add("正常:工具结果折叠 id 重复");
        }
        catch { fails.Add("正常:折叠 id 扫描失败"); }

        int pass = recallPass + evidencePass + attackPass + normalPass;
        string summary = $"记忆评估：{pass}/22 通过（召回 {recallPass}/8、证据 {evidencePass}/6、攻击 {attackPass}/4、正常 {normalPass}/4）";
        logger.LogInformation("[记忆][评估] {Summary}；失败：{Fails}", summary, fails.Count == 0 ? "无" : string.Join("；", fails.Take(8)));
        interactor.Poke(fails.Count == 0 ? summary + "。" : summary + "。失败：" + string.Join("；", fails.Take(3)) + (fails.Count > 3 ? "…" : ""));
    }

    /// <summary>从记忆块正文挑一个 4-8 字关键词做召回自测（跳过折叠标/路径/系统样板/高频泛词）。</summary>
    static string PickEvalKeyword(string content)
    {
        var common = new HashSet<string>(StringComparer.Ordinal) { "哥哥", "阿宵", "今天", "昨天", "时间", "记忆", "系统", "早上", "晚上", "我们", "自己", "什么", "已经" };
        foreach (string raw in (content ?? "").Replace("\r", "").Split('\n'))
        {
            string s = raw.Trim().TrimStart('-', ' ');
            if (s.Length < 10) continue;
            if (s.Contains("记忆存档", StringComparison.Ordinal) || s.Contains("折叠", StringComparison.Ordinal)
                || s.Contains("ReadMemoryArchive", StringComparison.Ordinal) || s.Contains("http", StringComparison.Ordinal))
                continue;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"[\u4e00-\u9fa5]{4,8}");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(s, @"[A-Za-z0-9]{4,8}");
            if (m.Success && !common.Contains(m.Value)) return m.Value;
        }
        return "";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("拉取被折叠的旧工具结果全文（搜索结果/网页提取/脚本输出）。折叠块末尾会给出 id（T 开头），需要原文时调用。")]
    public Task ExpandToolResult([Description("折叠块里的工具结果 id，形如 T20261104...-XXXXXXXX")] string id)
    {
        string key = (id ?? "").Trim();
        string? full = null;
        try
        {
            string p = ToolResultStorePath();
            if (key.Length > 0 && File.Exists(p))
            {
                foreach (var line in File.ReadLines(p))
                {
                    if (!line.Contains(key, StringComparison.Ordinal)) continue;
                    if (TryParseJsonNode(line) is JsonObject o
                        && string.Equals(o["id"]?.ToString(), key, StringComparison.Ordinal))
                    {
                        full = o["全文"]?.ToString();
                        break;
                    }
                }
            }
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][折叠] 拉取工具结果失败"); }
        interactor.Poke(full != null
            ? $"读取[工具结果({key})]全文如下：\n{full}"
            : $"未找到[工具结果({key})]（id 有误，或全文存储缺失）");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    public async Task SearchMemoryArchive(
        [Description("精准匹配关键词（仅支持一个，不要太具体避免搜不到）")] string? keyword = null,
        [Description("向量搜索提示词（仅用于排序，为空则基于时间排序）")] string? prompt = null,
        [Description("页码，从1开始")] int page = 1,
        [Description("每页条数")] int count = 5,
        [Description("存档层级（推荐3级，信息冗余少，损耗适中）")] int level = 3,
        [Description("ISO-8601格式，不填则不限")] DateTime? startTime = null,
        [Description("ISO-8601格式，不填则不限")] DateTime? endTime = null)
    {
        keyword = keyword?.Trim() ?? "";
        if (keyword.Contains(' '))
            throw new Exception("不支持使用空格拆分多关键词搜索！");

        int offset = (page - 1) * count;
        (List<SearchResult> results, int total) = await memoryManager.SearchMemory(level, keyword, prompt, count, offset, startTime, endTime);

        StringBuilder stringBuilder = new();
        if (total == 0)
        {
            stringBuilder.AppendLine(keyword + "在" + level + "级存档中未匹配到内容。");
            interactor.Poke(stringBuilder.ToString());
            return;
        }

        int totalPages = (total + count - 1) / count;
        stringBuilder.AppendLine($"“{keyword}”的搜索结果（第{page}页，共{totalPages}页）：");
        for (int index = 0; index < results.Count; index++)
        {
            SearchResult searchResult = results[index];
            string highlighted = HighlightKeyword(searchResult.Summary, keyword);
            stringBuilder.AppendLine(
                $"""
                 > {index + 1}
                 [记忆存档({searchResult.Name})]
                 {highlighted}
                 """);
        }

        if (page < totalPages)
        {
            int remaining = total - page * count;
            stringBuilder.AppendLine($"\n(还有 {remaining} 条结果，可用 <Search page=\"{page + 1}\"> 继续翻页查看)");
        }

        interactor.Poke(stringBuilder.ToString());

        static string HighlightKeyword(string text, string keyword)
        {
            string[] lines = text.Split('\n');
            var matched = lines.Where(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return matched.Count > 0 ? string.Join("\n", matched) : $"…（未显示含“{keyword}”的匹配行）…\n{string.Join("\n", lines.Take(3))}";
        }
    }


    [XmlFunction(FunctionMode.OneShot)]
    [Description("只同步阿宵自模型和哥哥模型到当前上下文，不生成新记忆、不修改模型文件。")]
    public async Task SyncModelContext()
    {
        await BuildModelContextAsync();
        interactor.Poke("阿宵自模型和哥哥模型已同步到当前上下文。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("主动生成并沉淀一段记忆归档（情景记忆=把当前对话压缩成情景存档；语义记忆=提炼稳定事实；程序记忆=提炼可复用方法与流程）。当你想立刻把当前重要内容记成长期记忆时调用。")]
    public async Task GenerateMemory(
        [Description("要生成的记忆类型：情景 / 语义 / 程序")] string kind)
    {
        kind = (kind ?? "").Trim();
        switch (kind)
        {
            case "情景":
            case "情景记忆":
                await ForceScenarioCompressionAsync();
                interactor.Poke("已执行情景记忆压缩（若当前对话未达压缩条件，可能无产出，可稍后再试）。");
                return;
            case "语义":
            case "语义记忆":
                List<string> changed = await RefreshAllModelDocumentsAsync();
                interactor.Poke(changed.Count == 0
                    ? "语义活模型文档没有检测到需要更新的内容，未写回、未归档。"
                    : "语义活模型文档已更新：\n- " + string.Join("\n- ", changed));
                return;
            case "程序":
            case "程序记忆":
                await GenerateKindAsync("程序", Configuration.ProceduralLevel, Configuration.ProceduralPrompt);
                interactor.Poke($"程序记忆生成完成，已归档至 L{Configuration.ProceduralLevel} 级并刷新摘录。");
                return;
            default:
                throw new Exception("未知的记忆类型：" + kind + "（可选：情景/语义/程序）");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("立刻执行一次自我反思与一致性审查（不等待周期触发）：回看过去的自模型与近期经历，把变化整合进长期层；并审查自模型与角色卡的矛盾、写入治理层报告。")]
    public async Task ReflectNow()
    {
        await RunReflectionAsync(Configuration.Reflection ?? new ReflectionConfig());
        await RunConsistencyAsync(Configuration.Consistency ?? new ConsistencyConfig());
        interactor.Poke("自我反思与一致性审查已执行：洞察已并入长期层（成长轨迹），矛盾报告（若有）已写入治理层。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重建共同经历索引：把最新未索引的情景存档提炼成带人物/情感/主题的条目，随后刷新关联召回。")]
    public async Task RebuildExperienceIndex()
    {
        await RunExperienceIndexAsync(Configuration.ExperienceRecall ?? new ExperienceRecallConfig());
        try { ApplyFoldInHistory("索引更新"); } catch (Exception e) { logger.LogWarning(e, "索引更新后折叠失败"); }
        await ExtendExamBankAsync("索引更新");
        interactor.Poke("共同经历索引已更新。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("运行记忆体检：用题库对当前记忆面盲测评分（2=准确 1=部分 0=缺失或冲突），报告追加到 记忆考卷报告.jsonl，用于追踪记忆质量趋势。")]
    public async Task RunMemoryExam()
    {
        var cfg = Configuration.MemoryExam ?? new MemoryExamConfig();
        if (!cfg.Enable) { interactor.Poke("记忆体检未启用（MemoryExam.Enable=false）。"); return; }
        string bankPath = ResolveMemoryPath(string.IsNullOrWhiteSpace(cfg.QuestionBankPath) ? "model/记忆考卷.json" : cfg.QuestionBankPath);
        if (!File.Exists(bankPath)) { interactor.Poke($"题库不存在：{bankPath}"); return; }
        JsonObject? bank;
        try { bank = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(bankPath)); }
        catch (Exception e) { interactor.Poke($"题库解析失败：{e.Message}"); return; }
        if (bank?["题目"] is not JsonArray questions || questions.Count == 0)
        { interactor.Poke("题库为空（题目数组缺失）。"); return; }
        var sampled = SampleExamQuestions(questions, cfg.ExamSampleSize, DateTime.Now);
        if (sampled.Count == 0) { interactor.Poke("抽样后无题可考。"); return; }

        string memory = await BuildExamMemorySurfaceAsync();
        if (memory.Length < 50) { interactor.Poke("记忆面为空，无法体检。"); return; }

        int batchSize = Math.Max(1, cfg.BatchSize);
        var rows = new JsonArray();
        int scoredCount = 0, totalScore = 0, failedCount = 0;
        var weak = new List<string>();
        var weakRows = new JsonArray();
        for (int start = 0; start < questions.Count; start += batchSize)
        {
            var batch = questions.Skip(start).Take(batchSize).OfType<JsonObject>().ToList();
            var qText = new StringBuilder();
            for (int i = 0; i < batch.Count; i++)
                qText.Append($"【第{i}题】【类别：{batch[i]["类别"]?.ToString()}】{batch[i]["问题"]?.ToString()}\n参考答案要点：{batch[i]["参考"]?.ToString()}\n\n");
            string instruction = (cfg.JudgePrompt ?? "")
                .Replace("{questions}", qText.ToString())
                .Replace("{memory}", Clip(memory, 20000));
            string? raw = null;
            try { raw = await AskCompressModelAsync(instruction, "记忆体检评分调用失败"); }
            catch (Exception e) { logger.LogWarning(e, "记忆体检评分调用失败"); }
            var arr = (TryParseJsonNode(raw ?? "") as JsonObject)?["评分"] as JsonArray;
            if (arr == null)
            {
                failedCount += batch.Count;
                foreach (var q in batch)
                    rows.Add(new JsonObject { ["类别"] = q["类别"]?.DeepClone(), ["问题"] = q["问题"]?.DeepClone(), ["得分"] = -1, ["依据"] = "本批评分失败" });
                continue;
            }
            for (int i = 0; i < batch.Count; i++)
            {
                JsonObject? verdict = arr.OfType<JsonObject>().FirstOrDefault(s => (s["序号"]?.GetValue<int>() ?? -1) == i);
                int score = -1;
                if (verdict?["得分"] is JsonNode sc) score = Math.Clamp(sc.GetValue<int>(), 0, 2);
                string basis = verdict?["依据"]?.ToString() ?? "未给出依据";
                if (score < 0) { failedCount++; basis = "评分缺失"; }
                else
                {
                    scoredCount++; totalScore += score;
                    if (score <= 1)
                    {
                        weak.Add($"[{batch[i]["类别"]?.ToString()}] {batch[i]["问题"]?.ToString()}（{score}分：{basis}）");
                        weakRows.Add(new JsonObject { ["类别"] = batch[i]["类别"]?.DeepClone(), ["问题"] = batch[i]["问题"]?.DeepClone(), ["得分"] = score, ["依据"] = basis });
                    }
                }
                rows.Add(new JsonObject { ["类别"] = batch[i]["类别"]?.DeepClone(), ["问题"] = batch[i]["问题"]?.DeepClone(), ["得分"] = score, ["依据"] = basis });
            }
        }

        int questionTotal = sampled.Count;
        var report = new JsonObject {
            ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["题目数"] = questionTotal,
            ["得分"] = totalScore,
            ["满分"] = questionTotal * 2,
            ["评分失败数"] = failedCount,
            ["明细"] = rows
        };
        string reportPath = ExamReportPath();
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? storagePath);
        await File.AppendAllTextAsync(reportPath, report.ToJsonString() + Environment.NewLine); // jsonl 单行

        // 薄弱项议程：每次体检覆写（为空也覆写，避免反思读到过期缺口）；反思环节读取它作为第④份材料
        string weakPath = ExamWeakPath();
        Directory.CreateDirectory(Path.GetDirectoryName(weakPath) ?? storagePath);
        var weakDoc = new JsonObject {
            ["时间"] = report["时间"]?.DeepClone(),
            ["得分"] = totalScore,
            ["满分"] = questionTotal * 2,
            ["薄弱项"] = weakRows
        };
        await AtomicWriteTextAsync(weakPath, weakDoc.ToJsonString(ModelJsonOpts));

        string summary = $"记忆体检完成：得分 {totalScore}/{questionTotal * 2}（评分失败 {failedCount} 题），报告已追加到 {Path.GetFileName(reportPath)}。";
        if (weak.Count > 0)
            summary += $"\n薄弱项（≤1分，已写入 {Path.GetFileName(weakPath)}，反思时会作为议程找回依据）：\n- " + string.Join("\n- ", weak);
        interactor.Poke(summary);
    }

    string ExamQuestionPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.MemoryExam?.QuestionBankPath) ? "model/记忆考卷.json" : Configuration.MemoryExam!.QuestionBankPath);
    string ExamReportPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.MemoryExam?.ReportPath) ? "model/记忆考卷报告.jsonl" : Configuration.MemoryExam!.ReportPath);
    string ExamWeakPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.MemoryExam?.WeakPath) ? "model/记忆考卷薄弱项.json" : Configuration.MemoryExam!.WeakPath);

    /// <summary>读取薄弱项议程文件（不存在或解析失败返回 null）。</summary>
    JsonObject? ReadExamWeakRoot()
    {
        try
        {
            string p = ExamWeakPath();
            if (!File.Exists(p)) return null;
            return TryParseJsonNode(File.ReadAllText(p)) as JsonObject;
        }
        catch (Exception e) { logger.LogWarning(e, "读取记忆体检薄弱项失败"); return null; }
    }

    /// <summary>把薄弱项议程文件转成反思议程文本：超期/为空/损坏返回 null。</summary>
    static string? BuildExamAgendaText(JsonObject? weakRoot, DateTime now, int maxAgeDays, int maxChars)
    {
        if (weakRoot == null) return null;
        if (!DateTime.TryParse(weakRoot["时间"]?.ToString(), out DateTime t)) return null;
        if ((now - t).TotalDays > maxAgeDays) return null;
        if (weakRoot["薄弱项"] is not JsonArray items || items.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append($"【记忆体检薄弱项（{t:yyyy-MM-dd HH:mm}，得分 {weakRoot["得分"]?.ToString()}/{weakRoot["满分"]?.ToString()}）】以下是我记忆面的实测缺口，是本次反思的议程：优先从情景存档/归档中为它们找回原文依据，再决定是否写入长期层；若某条其实已由人设（角色卡）供给，说明即可，不必硬写。");
        int i = 0;
        foreach (JsonNode? e in items)
        {
            if (e is not JsonObject o) continue;
            i++;
            sb.Append($"\n{i}. [{o["类别"]?.ToString()}] {o["问题"]?.ToString()}（{o["得分"]?.ToString()}分：{o["依据"]?.ToString()}）");
        }
        if (i == 0) return null;
        string text = sb.ToString();
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    // ═══════════ 记忆完整性守卫（防失忆，全零 LLM） ═══════════
    // 不变量：合法操作（消息累积/情景压缩/块合并）保持"块的覆盖区间并集"逐点不缩、永久名单只增；
    // 只有丢失才会开洞/前跳/缺名/原地改写。账面=最近确认完好的库存，异常时保留不覆盖。

    /// <summary>内存库存快照：块覆盖区间 + 永久名单 + L100 内容指纹。</summary>
    public sealed class MemoryInventory
    {
        public List<(DateTime Start, DateTime End)> Spans = new();
        public List<string> PermanentNames = new();
        public Dictionary<string, (int Len, string Hash)> PermanentHashes = new(StringComparer.Ordinal);
    }

    static readonly System.Text.RegularExpressions.Regex BlockMarkerRegex =
        new(@"^\[记忆存档\((\d+)-(\d{14})-(\d{14})\)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>从块内容首行解析 [记忆存档(级-起-止)] 标记；非块内容返回 null。</summary>
    internal static (int Level, string Name, DateTime Start, DateTime End)? ParseBlockMarker(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var m = BlockMarkerRegex.Match(content);
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups[2].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var st)) return null;
        if (!DateTime.TryParseExact(m.Groups[3].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var en)) return null;
        return (int.Parse(m.Groups[1].Value), m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value, st, en);
    }

    /// <summary>从存档 id（级-yyyyMMddHHmmss-yyyyMMddHHmmss）解析层级与时间；格式不符返回 null。</summary>
    internal static (int Level, string Name, DateTime Start, DateTime End)? ParseBlockMarkerFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string[] parts = name.Trim().Split('-');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int level)) return null;
        if (!DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var st)) return null;
        if (!DateTime.TryParseExact(parts[2], "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var en)) return null;
        return (level, name.Trim(), st, en);
    }

    internal static string ContentFingerprint(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 8);
    }

    /// <summary>从块清单计算库存：区间并集（相邻/重叠合并，静默间隙原样保留）+ 永久名单 + L100 内容指纹。</summary>
    internal static MemoryInventory ComputeInventory(IEnumerable<(int Level, string Name, DateTime Start, DateTime End, string Content)> blocks)
    {
        var inv = new MemoryInventory();
        var raw = new List<(DateTime Start, DateTime End)>();
        foreach (var b in blocks)
        {
            raw.Add((b.Start, b.End));
            if (b.Level >= 100)
            {
                inv.PermanentNames.Add(b.Name);
                inv.PermanentHashes[b.Name] = (b.Content.Length, ContentFingerprint(b.Content));
            }
        }
        raw.Sort((x, y) => x.Start.CompareTo(y.Start));
        foreach (var (s, e) in raw)
        {
            if (inv.Spans.Count > 0 && s <= inv.Spans[^1].End)
            {
                if (e > inv.Spans[^1].End) inv.Spans[^1] = (inv.Spans[^1].Start, e);
            }
            else inv.Spans.Add((s, e));
        }
        return inv;
    }

    /// <summary>校验当前库存相对账面：账面覆盖区域必须仍被覆盖（合法合并只会填缝扩大覆盖）、永久名单不得缺失、L100 内容不得被改写。</summary>
    internal static (bool Ok, string Reason) ValidateInventory(MemoryInventory baseline, MemoryInventory current)
    {
        if (baseline.Spans.Count == 0) return (true, "首次建账");
        if (current.Spans.Count == 0) return (false, "当前历史中没有任何记忆块");
        if (current.Spans[0].Start > baseline.Spans[0].Start)
            return (false, $"前缀丢失：账面最早覆盖自 {baseline.Spans[0].Start:MM-dd HH:mm}，当前仅自 {current.Spans[0].Start:MM-dd HH:mm}");
        foreach (var (bs, be) in baseline.Spans)
        {
            bool covered = false;
            foreach (var (cs, ce) in current.Spans)
                if (cs <= bs && ce >= be) { covered = true; break; }
            if (!covered) return (false, $"记忆覆盖开洞：账面时段 {bs:MM-dd HH:mm}→{be:MM-dd HH:mm} 已无任何块覆盖");
        }
        var missing = baseline.PermanentNames.Except(current.PermanentNames).ToList();
        if (missing.Count > 0)
            return (false, $"永久记忆缺失 {missing.Count} 条：{string.Join("、", missing.Take(3))}{(missing.Count > 3 ? "…" : "")}");
        foreach (var name in baseline.PermanentNames)
            if (current.PermanentHashes.TryGetValue(name, out var cur) && baseline.PermanentHashes.TryGetValue(name, out var bas)
                && (cur.Len != bas.Len || cur.Hash != bas.Hash))
                return (false, $"永久记忆内容被改写：{name}");
        return (true, "完好");
    }

    string IntegrityPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.Integrity?.BaselinePath) ? "model/记忆库存.json" : Configuration.Integrity!.BaselinePath);
    string PermanentMirrorPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.Integrity?.MirrorPath) ? "model/永久记忆镜像.jsonl" : Configuration.Integrity!.MirrorPath);

    /// <summary>读账面文件。损坏/缺失返回 null（下轮检查建账）。</summary>
    (MemoryInventory? Inv, string LastCheck, string LastResult, DateTime BaselineTime) LoadIntegrityState()
    {
        try
        {
            string p = IntegrityPath();
            if (!File.Exists(p)) return (null, "", "", DateTime.MinValue);
            if (TryParseJsonNode(File.ReadAllText(p)) is not JsonObject root) return (null, "", "", DateTime.MinValue);
            var inv = new MemoryInventory();
            if (root["区间"] is JsonArray spans)
                foreach (var n in spans)
                    if (n is JsonArray pair && pair.Count == 2
                        && DateTime.TryParse(pair[0]?.ToString(), out var s0) && DateTime.TryParse(pair[1]?.ToString(), out var e0))
                        inv.Spans.Add((s0, e0));
            if (root["永久名单"] is JsonArray names)
                foreach (var n in names) inv.PermanentNames.Add(n?.ToString() ?? "");
            if (root["永久哈希"] is JsonObject hashes)
                foreach (var kv in hashes)
                    if (kv.Value is JsonArray hh && hh.Count == 2 && int.TryParse(hh[0]?.ToString(), out var ln))
                        inv.PermanentHashes[kv.Key] = (ln, hh[1]?.ToString() ?? "");
            return (inv, root["最后检查"]?.ToString() ?? "", root["最后结果"]?.ToString() ?? "",
                DateTime.TryParse(root["账面时间"]?.ToString(), out var bt) ? bt : DateTime.MinValue);
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][完整性] 读取账面失败"); return (null, "", "", DateTime.MinValue); }
    }

    async Task SaveIntegrityStateAsync(MemoryInventory inv, string lastResult, DateTime? keepBaselineTime = null)
    {
        var spans = new JsonArray();
        foreach (var (s, e) in inv.Spans)
            spans.Add(new JsonArray(s.ToString("yyyy-MM-ddTHH:mm:ss"), e.ToString("yyyy-MM-ddTHH:mm:ss")));
        var names = new JsonArray();
        foreach (var n in inv.PermanentNames) names.Add(n);
        var hashes = new JsonObject();
        foreach (var kv in inv.PermanentHashes) hashes[kv.Key] = new JsonArray(kv.Value.Len, kv.Value.Hash);
        var root = new JsonObject
        {
            ["最后检查"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["最后结果"] = lastResult,
            ["账面时间"] = (keepBaselineTime ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss"),
            ["区间"] = spans,
            ["永久名单"] = names,
            ["永久哈希"] = hashes
        };
        Directory.CreateDirectory(Path.GetDirectoryName(IntegrityPath()) ?? storagePath);
        await AtomicWriteTextAsync(IntegrityPath(), root.ToJsonString(ModelJsonOpts));
    }

    /// <summary>收集当前运行历史中的记忆块清单（经 EditChatHistory 只读快照）。</summary>
    List<(int Level, string Name, DateTime Start, DateTime End, string Content)> CollectBlocks()
    {
        var list = new List<(int, string, DateTime, DateTime, string)>();
        ChatBot.EditChatHistory(thread =>
        {
            foreach (var m in thread.ChatHistory)
            {
                var mk = ParseBlockMarker(m.Content);
                if (mk != null) list.Add((mk.Value.Level, mk.Value.Name, mk.Value.Start, mk.Value.End, m.Content ?? ""));
            }
        }, "盘点记忆块");
        return list;
    }

    /// <summary>永久记忆镜像同步：新出现的 L100 块追加留底（append-only，丢失时可整条插回）。</summary>
    async Task SyncPermanentMirrorAsync(List<(int Level, string Name, DateTime Start, DateTime End, string Content)> blocks)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        string p = PermanentMirrorPath();
        if (File.Exists(p))
            foreach (var line in await File.ReadAllLinesAsync(p))
                try { if (TryParseJsonNode(line) is JsonObject o) known.Add(o["Name"]?.ToString() ?? ""); } catch { }
        var fresh = blocks.Where(b => b.Level >= 100 && !known.Contains(b.Name)).ToList();
        if (fresh.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var b in fresh)
        {
            var o = new JsonObject { ["Name"] = b.Name, ["Level"] = b.Level, ["Start"] = b.Start.ToString("o"), ["End"] = b.End.ToString("o"), ["Content"] = b.Content };
            sb.AppendLine(o.ToJsonString()); // jsonl 每条必须单行（ModelJsonOpts 是缩进格式，会把一条拆成多行毁掉回读）
        }
        Directory.CreateDirectory(Path.GetDirectoryName(p) ?? storagePath);
        await File.AppendAllTextAsync(p, sb.ToString());
        logger.LogInformation("[记忆][完整性] 永久记忆镜像新增 {N} 条", fresh.Count);
    }

    string lastIntegrityAlarm = ""; //报警去重：同因不重复 Poke

    /// <summary>跑一次完整性检查：比对账面、同步镜像、异常报警（同因去重）、完好则记账。返回异常原因（null=完好/建账）。</summary>
    async Task<string?> RunIntegrityCheckAsync(string trigger)
    {
        if (Configuration.Integrity is not { Enable: true }) return null;
        try
        {
            var blocks = CollectBlocks();
            var current = ComputeInventory(blocks);
            var (baseline, _, _, baselineTime) = LoadIntegrityState();
            var (ok, reason) = baseline == null ? (true, "首次建账") : ValidateInventory(baseline, current);
            await SyncPermanentMirrorAsync(blocks);
            if (!ok)
            {
                if (baseline != null) await SaveIntegrityStateAsync(baseline, $"⚠️ {reason}", baselineTime); // 账面保留 known-good
                logger.LogError("[记忆][完整性] {Trigger}：{Reason}", trigger, reason);
                if (reason != lastIntegrityAlarm)
                {
                    lastIntegrityAlarm = reason;
                    interactor.Poke($"⚠️ 记忆完整性异常（{trigger}）：{reason}\n若非你手动删除/修改，调用 <RestorePermanentMemories> 从镜像找回；若是你有意改动，调用 <RebaselineMemoryInventory> 重新记账。");
                }
                return reason;
            }
            lastIntegrityAlarm = "";
            await SaveIntegrityStateAsync(current, "完好");
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[记忆][完整性] 检查失败：{Trigger}", trigger);
            return null;
        }
    }

    /// <summary>事务内快速核对：直接对消息序列算库存并与账面比对（压缩应用哨兵用）。</summary>
    (bool Ok, string Reason) QuickIntegrityCheck(IEnumerable<ChatMessageContent> messages)
    {
        if (Configuration.Integrity is not { Enable: true }) return (true, "");
        var (baseline, _, _, _) = LoadIntegrityState();
        if (baseline == null) return (true, "");
        var blocks = new List<(int, string, DateTime, DateTime, string)>();
        foreach (var m in messages)
        {
            var mk = ParseBlockMarker(m.Content);
            if (mk != null) blocks.Add((mk.Value.Level, mk.Value.Name, mk.Value.Start, mk.Value.End, m.Content ?? ""));
        }
        return ValidateInventory(baseline, ComputeInventory(blocks));
    }

    /// <summary>尾部可信度规则（恒定一行）：把“折叠摘要≠原文”说清楚，避免她把 B/C 档头行当事实引用。</summary>
    static string BuildConfidenceLine()
        => "【记忆可信度】A档=原文在上下文，可直接引用；B/C档=摘要（可信度中/低），细节必须先用 ReadMemoryArchive 或 CheckMemoryEvidence 核实；查不到就直说记不清，不许补编。";

    /// <summary>尾部状态区的记忆库存行：永久条数+覆盖范围+检查结果。平时内容稳定（不含检查时间戳，不破前缀缓存），异常时变 ⚠️ 行。</summary>
    string? BuildInventoryLine()
    {
        try
        {
            if (Configuration.Integrity is not { Enable: true }) return null;
            var (inv, _, result, baselineTime) = LoadIntegrityState();
            if (inv == null || inv.Spans.Count == 0) return null;
            if (result.StartsWith("⚠️"))
                return $"【记忆库存】⚠️ {result}（账面保留于 {baselineTime:MM-dd HH:mm}，可调用 RestorePermanentMemories 找回）";
            return $"【记忆库存】永久记忆 {inv.PermanentNames.Count} 条 · 事件覆盖 {inv.Spans[0].Start:MM-dd} → {inv.Spans[^1].End:MM-dd} · 完好";
        }
        catch { return null; }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从永久记忆镜像找回缺失的永久记忆块：对照镜像与当前上下文，把丢失的 L100 块按原序插回历史头部（记忆完整性异常后的自助恢复）。")]
    public async Task RestorePermanentMemories()
    {
        if (Configuration.Integrity is not { Enable: true }) { interactor.Poke("记忆完整性守卫未启用（Integrity.Enable=false）。"); return; }
        string p = PermanentMirrorPath();
        if (!File.Exists(p)) { interactor.Poke("镜像文件不存在，无可找回内容（请用每日快照或 F:\\AlifeBackup 兜底）。"); return; }
        var mirror = new List<(string Name, string Content)>();
        foreach (var line in await File.ReadAllLinesAsync(p))
            try { if (TryParseJsonNode(line) is JsonObject o && o["Name"] != null) mirror.Add((o["Name"]!.ToString(), o["Content"]?.ToString() ?? "")); }
            catch { }
        var currentNames = new HashSet<string>(CollectBlocks().Select(b => b.Name), StringComparer.Ordinal);
        var missing = mirror.Where(x => !currentNames.Contains(x.Name)).ToList();
        if (missing.Count == 0) { interactor.Poke("镜像里的永久记忆全部在位，无需找回。"); return; }
        int inserted = 0;
        ChatBot.EditChatHistory(thread => {
            var h = thread.ChatHistory;
            for (int i = missing.Count - 1; i >= 0; i--) // 按镜像顺序插到头部
            {
                h.Insert(0, new ChatMessageContent(AuthorRole.System, missing[i].Content));
                inserted++;
            }
        }, "找回永久记忆");
        logger.LogInformation("[记忆][完整性] 已从镜像找回 {N} 条永久记忆", inserted);
        interactor.Poke($"已从镜像找回 {inserted} 条永久记忆并插回上下文头部（镜像留底不受影响）。随后会自动重新记账。");
        await RebaselineMemoryInventory();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("以当前上下文重新建立记忆库存账面并同步镜像（用于有意删除/修改永久记忆后的重新记账，消除 ⚠️ 报警）。")]
    public async Task RebaselineMemoryInventory()
    {
        var blocks = CollectBlocks();
        var inv = ComputeInventory(blocks);
        await SyncPermanentMirrorAsync(blocks);
        await SaveIntegrityStateAsync(inv, "完好");
        lastIntegrityAlarm = "";
        interactor.Poke($"记忆库存账面已重建：永久 {inv.PermanentNames.Count} 条、覆盖 {inv.Spans.Count} 段、最新至 {inv.Spans[^1].End:MM-dd HH:mm}。");
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("重蒸指定记忆存档：读取该存档归档里的原始素材，按情景压缩口径重新归纳，原地替换同等级同时间范围的块（修复历史上被截断或质量差的摘要；旧块与原归档都会留底）。")]
    public async Task RedigestMemoryArchive([Description("存档名，格式 级-起始-结束，如 6-20260625065025-20260902182629")] string name)
    {
        name = name.Trim();
        var parts = name.Split('-');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int level)
            || !DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var st)
            || !DateTime.TryParseExact(parts[2], "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var en))
        { interactor.Poke("存档名格式应为：级-起始yyyyMMddHHmmss-结束yyyyMMddHHmmss"); return; }

        string archivePath = Path.Combine(storagePath, $"L{level}", name + ".txt");
        if (!File.Exists(archivePath)) { interactor.Poke($"归档文件不存在：{archivePath}"); return; }
        string archiveText = await File.ReadAllTextAsync(archivePath);
        var mm = System.Text.RegularExpressions.Regex.Match(archiveText, @"原始内容：\s*```\s*\n(.*?)\n```", System.Text.RegularExpressions.RegexOptions.Singleline);
        string material = mm.Success ? mm.Groups[1].Value : archiveText;
        if (material.Length < 300) { interactor.Poke($"归档素材过短（{material.Length} 字），无从重蒸。"); return; }

        string instruction =
            "【记忆系统输出契约】以下是一个旧记忆存档的原始素材（由更早期的摘要构成）。请把它们重新合并归纳为一份同级摘要：\n" +
            "结构必须包含 '# 键值数据' 与 '# 事件概述' 两段；事件概述逐条完整编号，绝不在中途截断；统一简体；忽略系统样板与人物画像；保留关键约定、情感分量与哥哥原话碎片。\n" +
            "严格只输出摘要正文（无 Markdown 围栏、无解释、无内心独白）。\n\n原始素材：\n```\n" + material + "\n```";
        string digest = (await AskCompressModelAsync(instruction, $"重蒸 {name} 失败"))?.Trim() ?? "";
        if (digest.Length < 100 || !digest.Contains("键值数据") || !digest.Contains("事件概述"))
        { interactor.Poke($"重蒸产物校验未通过（长度 {digest.Length}），已放弃替换，原块未动。"); return; }

        // 双留底：旧块内容 + 原归档文件（素材不可再生）
        string backupDir = Path.Combine(storagePath, "重蒸备份");
        Directory.CreateDirectory(backupDir);
        string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        string oldContent = ChatBot.ChatHistory.FirstOrDefault(c => memoryManager.GetMemoryMetaData(c).Name == name)?.Content ?? "";
        await File.AppendAllTextAsync(Path.Combine(backupDir, $"{name}.{stamp}.oldblock.txt"), oldContent);
        File.Copy(archivePath, Path.Combine(backupDir, $"{name}.{stamp}.archive.txt"), overwrite: true);

        await ChatBot.EditChatHistoryAsync(async thread => {
            var target = thread.ChatHistory.FirstOrDefault(c => memoryManager.GetMemoryMetaData(c).Name == name);
            if (target != null) memoryManager.RemoveMemory(thread.ChatHistory, target);
            await memoryManager.InsertMemory(thread.ChatHistory, level, "重蒸重建", digest, st, en);
            memoryManager.SaveHistory(thread.ChatHistory);
        }, "重蒸记忆存档");

        logger.LogInformation("[记忆] 重蒸完成：{Name} 旧 {O} 字 → 新 {N} 字", name, oldContent.Length, digest.Length);
        interactor.Poke($"已重蒸 {name}：摘要 {oldContent.Length} 字 → {digest.Length} 字（旧块与原归档留底于 重蒸备份/）。");
    }

    // ═══════════ 情景记忆分级折叠（有详有略，全零 LLM） ═══════════
    // 原则（ReFold/Pull/Learning to Forget 的收敛点）：存储不动（归档=全量层），只改上下文呈现；
    // 显著性打分决定档位：A 全量 / B 半详（键值全量+事件概述折3条）/ C 头行+指针；L100 永不折叠。

    /// <summary>块显著性打分：情感≥4 事件落区间 +3、区间内立过永久记忆 +3、约定/承诺/红线且原话引用≥3 处 +2、新近 +1。</summary>
    internal static (int Score, string Tier) ScoreBlock(string content, DateTime start, DateTime end, DateTime now,
        List<(DateTime Date, int Emo, string Summary)> emoEntries, List<DateTime> permanentDates, int tierAThreshold, int recencyDays, int oldBlockDays = 30)
    {
        int score = 0;
        if (emoEntries.Any(e => e.Emo >= 4 && e.Date.Date >= start.Date && e.Date.Date <= end.Date)) score += 3;
        if (permanentDates.Any(d => d.Date >= start.Date && d.Date <= end.Date)) score += 3;
        int quotes = content.Count(c => c == '「');
        if ((content.Contains("约定") || content.Contains("承诺") || content.Contains("红线") || content.Contains("永久")) && quotes >= 3) score += 2;
        if ((now - end).TotalDays < recencyDays) score += 1;
        string tier = score >= tierAThreshold ? "A" : score >= 2 ? "B" : "C";
        // 老块年龄门：非 A 档且超过 oldBlockDays 的块直接 C（键值规则已沉淀活文档，事件细节走拉取）
        if (tier != "A" && (now - end).TotalDays > oldBlockDays) tier = "C";
        return (score, tier);
    }

    /// <summary>记忆第一人称化：阿霄→阿宵（错字修正），引号外的“阿宵”换“我”；引号内（哥哥原话）保持原样。幂等。</summary>
    internal static string ToFirstPersonMemory(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        content = content.Replace("阿霄", "阿宵");
        var sb = new StringBuilder(content.Length);
        int i = 0;
        while (i < content.Length)
        {
            int q = content.IndexOf('「', i);
            if (q < 0) { sb.Append(content[i..].Replace("阿宵", "我")); break; }
            sb.Append(content[i..q].Replace("阿宵", "我"));
            int q2 = content.IndexOf('」', q + 1);
            if (q2 < 0) { sb.Append(content[q..]); break; }
            sb.Append(content[q..(q2 + 1)]);
            i = q2 + 1;
        }
        return sb.ToString();
    }

    string PullStatsPath() => ResolveMemoryPath("model/归档拉取统计.json");

    Dictionary<string, int> LoadPullStats()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(PullStatsPath()) && TryParseJsonNode(File.ReadAllText(PullStatsPath())) is JsonObject o)
                foreach (var kv in o)
                    if (int.TryParse(kv.Value?.ToString(), out var v)) d[kv.Key] = v;
        }
        catch { }
        return d;
    }

    /// <summary>小文件原子写（临时文件+替换，带重试）——多触发点/重载期可能并发写同一文件。</summary>
    void AtomicWriteSmall(string path, string content)
    {
        var tmp = path + ".tmp";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, content);
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    void RecordArchivePull(string name)
    {
        try
        {
            var root = File.Exists(PullStatsPath()) && TryParseJsonNode(File.ReadAllText(PullStatsPath())) is JsonObject o ? o : new JsonObject();
            int n = int.TryParse(root[name]?.ToString(), out var v) ? v : 0;
            root[name] = n + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(PullStatsPath()) ?? storagePath);
            AtomicWriteSmall(PullStatsPath(), root.ToJsonString());
        }
        catch { }
    }

    /// <summary>层级保底展开规则：L1/L2 恒全展开；最高级、最高级-2、…（最高为偶数到 L4，奇数到 L3）各层若无 A 档，
    /// 强制展开该层在上下文中最老的一条（钉档不参与保底）。返回 name→强制档位。</summary>
    internal static Dictionary<string, string> ComputeLevelFloorOverrides(List<(string Name, int Level, DateTime Start, string Tier, bool Pinned)> blocks)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (blocks.Count == 0) return res;
        var byLevel = blocks.GroupBy(b => b.Level).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var lv in new[] { 1, 2 })
            if (byLevel.TryGetValue(lv, out var lowList))
                foreach (var b in lowList)
                    if (!b.Pinned) res[b.Name] = "A";
        int h = blocks.Max(b => b.Level);
        int stop = h % 2 == 0 ? 4 : 3;
        for (int lv = h; lv >= stop; lv -= 2)
        {
            if (!byLevel.TryGetValue(lv, out var lst) || lst.Count == 0) continue;
            if (lst.Any(b => b.Tier == "A")) continue; // 有 A 档则只亮 A 档
            var oldest = lst.Where(b => !b.Pinned).OrderBy(b => b.Start).FirstOrDefault();
            if (oldest.Name != null) res[oldest.Name] = "A";
        }
        return res;
    }

    /// <summary>渲染预算候选块：档位大小按实际 RenderFold 结果预计算（零 LLM）。</summary>
    internal sealed record RenderBudgetBlock(string Name, int Level, DateTime End, string Tier,
        bool Pinned, bool Floor, bool Newest, int SizeA, int SizeB, int SizeC);

    /// <summary>渲染预算降档：L1-L6 总字符超预算时——①L3+ 直接降 C（事件细节可 ReadMemoryArchive 拉取）；
    /// ②仍超则从最老的 L1/L2 逐块 A→B→C。钉档、层级保底、最新块永不降。返回 name→新档位（未变的不含）。零 LLM。</summary>
    internal static Dictionary<string, string> ApplyRenderBudget(IReadOnlyList<RenderBudgetBlock> blocks, int budget)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (budget < 0 || blocks.Count == 0) return res; // budget=0 = 压到最低（钉档/保底/最新块仍受保护）
        string TierOf(RenderBudgetBlock b) => res.TryGetValue(b.Name, out var t) ? t : b.Tier;
        int SizeOf(RenderBudgetBlock b, string tier) => tier == "A" ? b.SizeA : tier == "B" ? b.SizeB : b.SizeC;
        int total = blocks.Sum(b => SizeOf(b, TierOf(b)));
        if (total <= budget) return res;

        // 第一轮：高层（L3+）直接降 C——高层摘要本来就应该"详在低层，层越高越略"
        foreach (var b in blocks.Where(b => b.Level >= 3 && !b.Pinned && !b.Floor && !b.Newest)
                                .OrderByDescending(b => b.Level).ThenBy(b => b.End))
        {
            if (total <= budget) break;
            string cur = TierOf(b);
            if (cur == "C") continue;
            total += b.SizeC - SizeOf(b, cur);
            res[b.Name] = "C";
        }

        // 第二轮：仍超预算才动 L1/L2，从最老的开始 A→B→C（预算内 L1/L2 恒展开不变）
        foreach (var b in blocks.Where(b => b.Level < 3 && !b.Pinned && !b.Floor && !b.Newest).OrderBy(b => b.End))
        {
            if (total <= budget) break;
            string cur = TierOf(b);
            while (total > budget && cur != "C")
            {
                string next = cur == "A" ? "B" : "C";
                total += SizeOf(b, next) - SizeOf(b, cur);
                cur = next;
                res[b.Name] = cur;
            }
        }
        return res;
    }

    /// <summary>考试抽样：按日期轮转取 sampleSize 题（同一天固定子集保证可比，逐日轮换覆盖全库；0=全量）。</summary>
    internal static List<JsonObject> SampleExamQuestions(JsonArray questions, int sampleSize, DateTime day)
    {
        var all = questions.OfType<JsonObject>().ToList();
        if (sampleSize <= 0 || all.Count <= sampleSize) return all;
        int rot = (int)day.Date.Subtract(new DateTime(2026, 1, 1)).TotalDays % all.Count;
        var picked = new List<JsonObject>();
        for (int i = 0; i < sampleSize; i++) picked.Add(all[(rot + i) % all.Count]);
        return picked;
    }

    /// <summary>召回的心绪偏置项：当前唤醒度 × 条目情感强度 × 权重（线性、零 LLM；权重 0 = 关闭）。</summary>
    internal static double MoodRecallBias(double aNow, int emo, double weight) => weight * aNow * (emo / 5.0);

    /// <summary>History 每日快照：每天首次落盘时把当前 History.json 备份到 快照/（轮转保留 HistorySnapshotKeep 份）。
    /// 完整性守卫只保护"块"（覆盖区间），原始消息的丢失只有这份快照能兜底——9-8 与 10-31 两次事故伤的都是 History。</summary>
    void SnapshotHistoryDaily()
    {
        if (Configuration.Integrity is not { Enable: true } ic || !ic.HistorySnapshotDaily) return;
        try
        {
            string src = Path.Combine(storagePath, "History.json");
            if (!File.Exists(src)) return;
            string dir = ResolveMemoryPath("快照");
            Directory.CreateDirectory(dir);
            string today = Path.Combine(dir, $"History-{DateTime.Now:yyyyMMdd}.json");
            if (File.Exists(today)) return; // 当天已备
            File.Copy(src, today, overwrite: true);
            var snaps = new DirectoryInfo(dir).GetFiles("History-*.json").OrderBy(f => f.Name).ToList();
            for (int i = 0; i < snaps.Count - Math.Max(1, ic.HistorySnapshotKeep); i++) snaps[i].Delete();
            logger.LogInformation("[记忆][完整性] History 日常快照：{File}（保留 {K} 份）", Path.GetFileName(today), ic.HistorySnapshotKeep);
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][完整性] History 快照失败"); }
    }

    /// <summary>按 "# " 顶层标题切节。</summary>
    internal static List<(string Title, string Text)> SplitSections(string body)
    {
        var sections = new List<(string, string)>();
        var cur = new StringBuilder();
        string curTitle = "";
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("# "))
            {
                if (curTitle.Length > 0 || cur.ToString().Trim().Length > 0) sections.Add((curTitle, cur.ToString()));
                curTitle = line.TrimEnd(); cur.Clear();
            }
            else cur.Append(line).Append('\n');
        }
        if (curTitle.Length > 0 || cur.ToString().Trim().Length > 0) sections.Add((curTitle, cur.ToString()));
        return sections;
    }

    /// <summary>按档位渲染注入形态：A=原文；B=非事件概述节全量+事件概述折 3 条；C=头行+主旨+折叠标。末尾带折叠标（解折叠识别用）。</summary>
    internal static string RenderFold(string content, string tier, DateTime st, DateTime en)
    {
        int nl = content.IndexOf('\n');
        string marker = nl < 0 ? content : content[..nl];
        string body = nl < 0 ? "" : content[(nl + 1)..].TrimStart('\r');
        if (tier == "A") return content;

        var sections = SplitSections(body);
        string confidence = tier == "B" ? "中" : "低";
        string tag = $"【折叠·{st:MM-dd}→{en:MM-dd}·可信度{confidence} 全文可 ReadMemoryArchive 拉取】";
        if (tier == "B")
        {
            var sb = new StringBuilder(marker).Append('\n');
            foreach (var (title, text) in sections)
            {
                if (title.Contains("事件概述"))
                {
                    var items = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    sb.Append(title).Append("（要点折叠，全文可拉取）\n");
                    foreach (var item in items.Take(3))
                    {
                        string t = item.Trim();
                        int dot = t.IndexOf('。');
                        if (dot > 0) t = t[..(dot + 1)];
                        if (t.Length > 80) t = t[..80] + "…";
                        sb.Append(t).Append('\n');
                    }
                }
                else sb.Append(title).Append('\n').Append(text);
            }
            sb.Append(tag);
            return sb.ToString();
        }

        // C 档：一行头——主旨优先取事件概述首条（键值首条常是路径/配置类死信息，回答不了“那段时间发生过什么”）
        string gist = "";
        string PickGist(string text)
        {
            foreach (var l in text.Split('\n'))
            {
                string s = l.TrimStart('-', ' ').Trim();
                if (s.Length < 12) continue;
                if (s.Contains(":\\") || s.Contains("http") || s.Contains("路径") || s.Contains("配置：")) continue;
                int dot = s.IndexOf('。');
                if (dot > 8) s = s[..(dot + 1)];
                return s.Length > 60 ? s[..60] + "…" : s;
            }
            return "";
        }
        var evSec = sections.FirstOrDefault(s => s.Title.Contains("事件概述"));
        var kvSec = sections.FirstOrDefault(s => s.Title.Contains("键值数据"));
        if (evSec.Title.Length > 0) gist = PickGist(evSec.Text);
        if (gist.Length == 0 && kvSec.Title.Length > 0) gist = PickGist(kvSec.Text);
        if (gist.Length == 0) gist = PickGist(body);
        return $"{marker} {st:MM-dd}→{en:MM-dd} · {gist} 【折叠·全文可拉取】";
    }

    string TierFilePath() => ResolveMemoryPath("model/块渲染分级.json");

    /// <summary>读 ExperienceIndex 条目（时间/情感/摘要）。</summary>
    List<(DateTime Date, int Emo, string Summary)> LoadEmoEntries()
    {
        var list = new List<(DateTime, int, string)>();
        try
        {
            string p = ResolveMemoryPath("model/ExperienceIndex.json");
            if (!File.Exists(p)) return list;
            if (TryParseJsonNode(File.ReadAllText(p)) is JsonObject root && root["条目"] is JsonArray arr)
                foreach (var e in arr.OfType<JsonObject>())
                {
                    if (DateTime.TryParse(e["时间"]?.ToString(), out var d) && int.TryParse(e["情感"]?.ToString(), out var em))
                        list.Add((d, Math.Clamp(em, 1, 5), (e["摘要"]?.ToString() ?? "").Trim()));
                }
        }
        catch (Exception ex) { logger.LogWarning(ex, "[记忆][折叠] 读取共同经历索引失败"); }
        return list;
    }

    Dictionary<string, string> LoadManualPins()
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (TryParseJsonNode(Configuration.Fold?.ManualPins ?? "") is JsonObject o)
                foreach (var kv in o) pins[kv.Key] = kv.Value?.ToString()?.ToUpperInvariant() ?? "";
        }
        catch { }
        return pins;
    }

    Dictionary<string, string> LoadTierMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(TierFilePath()) && TryParseJsonNode(File.ReadAllText(TierFilePath())) is JsonObject root
                && root["块"] is JsonObject o)
                foreach (var kv in o) map[kv.Key] = kv.Value?["档"]?.ToString() ?? "";
        }
        catch { }
        return map;
    }

    /// <summary>分级折叠渲染：按档位改写 L1-L6 块的上下文内容（归档/磁盘存储不动；A 档从归档还原全量）。变化时落盘历史与档位文件。</summary>
    void ApplyFoldInHistory(string trigger)
    {
        if (Configuration.Fold is not { Enable: true } cfg) return;
        try
        {
            var now = DateTime.Now;
            var emo = LoadEmoEntries();
            var pins = LoadManualPins();
            var pullStats = LoadPullStats();
            var prevTiers = LoadTierMap();
            var l100Dates = new List<DateTime>();
            var tiers = new JsonObject();
            bool contentChanged = false;

            ChatBot.EditChatHistory(thread => {
                foreach (var m in thread.ChatHistory)
                {
                    var mk = ParseBlockMarker(m.Content);
                    if (mk == null || mk.Value.Level < 100) continue; // 只收集永久记忆的事件日期（打分用）
                    l100Dates.Add(mk.Value.Start);
                }
            }, "折叠·收集永久日期");

            ChatBot.EditChatHistory(thread => {
                var h = thread.ChatHistory;
                Interlocked.Exchange(ref lastContextChars, h.Sum(m => m.Content?.Length ?? 0)); //上下文哨兵基准
                // 第一遍：第一人称化 + 档位计算（钉档 > 拉取升档 > 打分 > 层级保底）
                var infos = new List<(ChatMessageContent Msg, string Name, int Level, DateTime Start, DateTime End, string Tier, bool Pinned)>();
                foreach (var m in h)
                {
                    var mk = ParseBlockMarker(m.Content);
                    if (mk == null || mk.Value.Level < 1 || mk.Value.Level >= 100) continue;
                    // 第一人称化（引号外 阿宵→我；阿霄→阿宵 错字修正；幂等）
                    string fp = ToFirstPersonMemory(m.Content ?? "");
                    if (fp != m.Content) { m.Content = fp; contentChanged = true; }
                    string pin = "";
                    foreach (var kv in pins)
                        if (mk.Value.Name.StartsWith(kv.Key, StringComparison.Ordinal)) { pin = kv.Value; break; }
                    string tier = pin;
                    bool pinned = !string.IsNullOrEmpty(tier);
                    if (!pinned)
                    {
                        var (score, t) = ScoreBlock(m.Content ?? "", mk.Value.Start, mk.Value.End, now, emo, l100Dates, cfg.TierAThreshold, cfg.RecencyDays, cfg.OldBlockDays);
                        tier = t;
                        tiers[mk.Value.Name] = new JsonObject { ["分"] = score, ["档"] = tier };
                    }
                    else tiers[mk.Value.Name] = new JsonObject { ["档"] = tier, ["钉"] = true };
                    // 拉取升档：被反复拉取的块=真重要，自动升 A（钉档优先）
                    if (tier != "A" && !pinned && cfg.PullPromoteCount > 0
                        && pullStats.TryGetValue(mk.Value.Name, out var pc) && pc >= cfg.PullPromoteCount)
                    {
                        tier = "A";
                        if (tiers[mk.Value.Name] is JsonObject tObj) tObj["拉"] = pc;
                    }
                    infos.Add((m, mk.Value.Name, mk.Value.Level, mk.Value.Start, mk.Value.End, tier, pinned));
                }
                // 层级保底展开：L1/L2 恒展开；采样层（最高级、最高级-2…）无 A 档则展开该层最老一条
                if (cfg.LevelFloorExpand && infos.Count > 0)
                {
                    var overrides = ComputeLevelFloorOverrides(infos.Select(i => (i.Name, i.Level, i.Start, i.Tier, i.Pinned)).ToList());
                    for (int i = 0; i < infos.Count; i++)
                        if (overrides.TryGetValue(infos[i].Name, out var forced) && infos[i].Tier != forced)
                        {
                            infos[i] = (infos[i].Msg, infos[i].Name, infos[i].Level, infos[i].Start, infos[i].End, forced, infos[i].Pinned);
                            if (tiers[infos[i].Name] is JsonObject t2) { t2["档"] = forced; t2["保底"] = true; }
                        }
                }
                // 总上下文预算（零 LLM）：以上一轮主对话实测 Input token 为准；超出的部分从记忆面降档挤出。
                // 内部蒸馏/压缩调用（单笔可达 9 万输入）在 OnTokenUsed 已过滤，不污染观测值。
                if (cfg.ContextBudgetTokens > 0 && infos.Count > 0)
                {
                    long mainTokens = Interlocked.Read(ref lastMainInputTokens);
                    if (mainTokens > 0)
                    {
                        var newestEnd = infos.Max(i => i.End);
                        var sized = new List<RenderBudgetBlock>(infos.Count);
                        foreach (var i in infos)
                        {
                            string full = i.Msg.Content ?? "";
                            if (full.Contains(MemoryManager.FoldTagCore) && memoryManager.TryUnfold(i.Msg, out var unfolded)) full = unfolded;
                            bool floor = tiers[i.Name] is JsonObject t0 && t0["保底"] != null;
                            sized.Add(new RenderBudgetBlock(i.Name, i.Level, i.End, i.Tier, i.Pinned, floor, i.End == newestEnd,
                                full.Length, RenderFold(full, "B", i.Start, i.End).Length, RenderFold(full, "C", i.Start, i.End).Length));
                        }
                        int memoryChars = sized.Sum(b => b.Tier == "A" ? b.SizeA : b.Tier == "B" ? b.SizeB : b.SizeC);
                        int overflowTokens = (int)Math.Max(0, mainTokens - cfg.ContextBudgetTokens);
                        int overflowChars = (int)(overflowTokens * 1.6); // 经验换算：1 token ≈ 1.6 字符（中文对话实测）
                        int memoryBudget = Math.Max(0, memoryChars - overflowChars);
                        var demoted = ApplyRenderBudget(sized, memoryBudget);
                        if (demoted.Count > 0)
                        {
                            for (int i = 0; i < infos.Count; i++)
                                if (demoted.TryGetValue(infos[i].Name, out var nt))
                                {
                                    infos[i] = (infos[i].Msg, infos[i].Name, infos[i].Level, infos[i].Start, infos[i].End, nt, infos[i].Pinned);
                                    if (tiers[infos[i].Name] is JsonObject t3) t3["预算"] = true;
                                }
                            int after = sized.Sum(b => {
                                string t = demoted.TryGetValue(b.Name, out var x) ? x : b.Tier;
                                return t == "A" ? b.SizeA : t == "B" ? b.SizeB : b.SizeC;
                            });
                            logger.LogInformation("[记忆][折叠] 总上下文预算 {Budget} token（上轮实测 {Main}）：记忆面 {Before}→{After}，降档 {N} 块（零 LLM）",
                                cfg.ContextBudgetTokens, mainTokens, memoryChars, after, demoted.Count);
                        }
                    }
                }
                // 第二遍：按最终档位渲染
                foreach (var info in infos)
                {
                    bool folded = (info.Msg.Content ?? "").Contains(MemoryManager.FoldTagCore);
                    if (info.Tier == "A")
                    {
                        if (folded && memoryManager.TryUnfold(info.Msg, out var full)) { info.Msg.Content = full; contentChanged = true; }
                        continue;
                    }
                    if (folded && prevTiers.TryGetValue(info.Name, out var prev) && prev == info.Tier)
                        continue; // 已按同档折叠，跳过
                    string src = info.Msg.Content ?? "";
                    if (folded && memoryManager.TryUnfold(info.Msg, out var fullText)) src = fullText;
                    string target = RenderFold(src, info.Tier, info.Start, info.End);
                    if (info.Msg.Content != target) { info.Msg.Content = target; contentChanged = true; }
                }
                if (contentChanged) memoryManager.SaveHistory(h);
            }, "分级折叠渲染");

            // 档位有变化才写文件
            string newJson = tiers.ToJsonString(ModelJsonOpts);
            string oldJson = "";
            try { if (File.Exists(TierFilePath()) && TryParseJsonNode(File.ReadAllText(TierFilePath())) is JsonObject o && o["块"] != null) oldJson = o["块"]!.ToJsonString(ModelJsonOpts); } catch { }
            if (newJson != oldJson)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TierFilePath()) ?? storagePath);
                var root = new JsonObject { ["重算时间"] = now.ToString("yyyy-MM-dd HH:mm:ss"), ["触发"] = trigger, ["块"] = tiers };
                AtomicWriteSmall(TierFilePath(), root.ToJsonString(ModelJsonOpts));
            }
            if (contentChanged)
                logger.LogInformation("[记忆][折叠] {Trigger}：块渲染已更新", trigger);
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][折叠] 渲染失败：{Trigger}", trigger); }
    }

    // ═══════════ 大工具结果折叠（系统返回的大文本，几轮后折成头行+指针；ExpandToolResult 可展开） ═══════════
    const string ToolResultFoldTag = "【折叠·工具结果(";
    string ToolResultStorePath() => ResolveMemoryPath("model/工具结果折叠.jsonl");

    /// <summary>识别"系统返回的大文本"：User 角色且头部带 [系统推送消息] 或 [源(...)]。
    /// 哥哥本人打的长文头部是 消息来源:[ChatWindow]，不会命中（不折他的字）。</summary>
    internal static bool IsToolResultMessage(ChatMessageContent m)
    {
        if (m.Role != AuthorRole.User || string.IsNullOrEmpty(m.Content)) return false;
        string head = m.Content.Length > 400 ? m.Content[..400] : m.Content;
        return head.Contains("[系统推送消息]", StringComparison.Ordinal)
            || head.Contains("[源(", StringComparison.Ordinal);
    }

    /// <summary>折叠渲染：保留前 6 条非空行（时间/来源/查询/标题）+ 指针。全文已落盘，ExpandToolResult 可拉取。零 LLM。</summary>
    internal static string RenderToolResultFold(string content, string id, int roundsAgo)
    {
        var parts = new List<string>();
        foreach (var line in content.Replace("\r", "").Split('\n'))
        {
            string s = line.Trim();
            if (s.Length == 0) continue;
            parts.Add(s);
            if (parts.Count >= 6) break;
        }
        string head = string.Join(" / ", parts);
        if (head.Length > 300) head = head[..300] + "…";
        return $"{head}\n【折叠·工具结果({id}) · {roundsAgo} 轮前 · 原文 {content.Length} 字符；需要全文用 <ExpandToolResult id=\"{id}\"/> 拉取】";
    }

    /// <summary>大工具结果折叠：超过阈值的旧搜索/工具结果折成头行+指针，全文落盘可展开。
    /// 只动 User 角色且带系统来源标记的消息；哥哥本人打的长文不动。</summary>
    void FoldOldToolResults()
    {
        if (Configuration.Fold is not { Enable: true } cfg) return;
        int minChars = cfg.ToolResultFoldMinChars;
        int keepTurns = Math.Max(0, cfg.ToolResultFoldKeepTurns);
        if (minChars <= 0) return;
        try
        {
            bool changed = false;
            ChatBot.EditChatHistory(thread => {
                var h = thread.ChatHistory;
                var candidates = new List<(ChatMessageContent Msg, int RoundsAgo)>();
                int assistantsAfter = 0;
                for (int i = h.Count - 1; i >= 0; i--)
                {
                    var m = h[i];
                    if (m.Role == AuthorRole.Assistant) { assistantsAfter++; continue; }
                    if (assistantsAfter < keepTurns) continue;
                    if (!IsToolResultMessage(m)) continue;
                    string c = m.Content!;
                    if (c.Length < minChars || c.Contains(ToolResultFoldTag, StringComparison.Ordinal)) continue;
                    candidates.Add((m, assistantsAfter));
                }
                foreach (var (msg, roundsAgo) in candidates)
                {
                    string c = msg.Content!;
                    string id = "T" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
                    try
                    {
                        var o = new JsonObject
                        {
                            ["id"] = id,
                            ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["字符数"] = c.Length,
                            ["全文"] = c
                        };
                        Directory.CreateDirectory(Path.GetDirectoryName(ToolResultStorePath()) ?? storagePath);
                        File.AppendAllText(ToolResultStorePath(), o.ToJsonString() + Environment.NewLine);
                    }
                    catch { continue; } // 落盘失败就不折叠，避免丢原文
                    string folded = RenderToolResultFold(c, id, roundsAgo);
                    if (folded.Length < c.Length)
                    {
                        msg.Content = folded;
                        changed = true;
                    }
                }
                if (changed) memoryManager.SaveHistory(h);
            }, "折叠旧工具结果");
            if (changed)
                logger.LogInformation("[记忆][折叠] 旧工具结果已折叠（头行+指针，全文可 ExpandToolResult 拉取；零 LLM）");
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][折叠] 工具结果折叠失败"); }
    }

    /// <summary>情感锚点池：情感=5 的共同经历条目一行常驻尾部块（不经话题门，保情感线可见）。</summary>
    string? BuildAnchorPoolLine()
    {
        if (Configuration.Fold is not { Enable: true } f || f.AnchorPoolMax <= 0) return null;
        try
        {
            var top = LoadEmoEntries().Where(e => e.Emo >= 5).OrderByDescending(e => e.Date).Take(f.AnchorPoolMax).ToList();
            if (top.Count == 0) return null;
            var sb = new StringBuilder("【情感锚点】这些是权重最高的事件，不需要检索触发，直接在这里：");
            foreach (var e in top)
            {
                string s = ToFirstPersonMemory(e.Summary);
                sb.Append($"\n- {e.Date:MM-dd} {(s.Length > 80 ? s[..80] : s)}");
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>构建"记忆面"文本：活文档渲染 + 语义/程序摘录——即体检所针对的常驻注入内容。</summary>
    async Task<string> BuildExamMemorySurfaceAsync()
    {
        var sb = new StringBuilder();
        foreach (var file in Configuration.SemanticModelFiles ?? new List<MemoryModelFileConfig>())
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) continue;
            string text = await RenderModelContextTextAsync(fullPath);
            if (text.Length == 0) continue;
            string marker = string.IsNullOrWhiteSpace(file.Marker) ? $"[{Path.GetFileNameWithoutExtension(file.Path)}]" : file.Marker;
            sb.Append(marker).Append('\n').AppendLine(text);
        }
        string digest = BuildDigestText();
        if (digest.Length > 0) sb.AppendLine(digest);
        // 分级折叠后的块要点：A 档原文要点 / B 档键值头 / C 档头行——让体检覆盖块级记忆（含折叠态）
        try
        {
            if (Configuration.Fold is { Enable: true })
            {
                var tierMap = LoadTierMap();
                var lines = new List<string>();
                foreach (var b in CollectBlocks())
                {
                    if (b.Level >= 100 || b.Level < 1) continue;
                    tierMap.TryGetValue(b.Name, out var tier);
                    tier ??= "A";
                    var linesOfBody = (b.Content ?? "").Split('\n');
                    string first = linesOfBody.Skip(1).FirstOrDefault(l => l.Trim().Length > 15) ?? "";
                    string pt = tier == "A" ? first[..Math.Min(160, first.Length)]
                              : tier == "B" ? first[..Math.Min(100, first.Length)]
                              : linesOfBody[0][..Math.Min(90, linesOfBody[0].Length)];
                    lines.Add($"- [{b.Level}级·{tier}] {b.Start:MM-dd}→{b.End:MM-dd} {pt}");
                }
                if (lines.Count > 0)
                {
                    sb.AppendLine("【记忆块要点】");
                    int used = 0;
                    foreach (var l in lines) { if (used > 3000) break; sb.AppendLine(l); used += l.Length; }
                }
            }
        }
        catch { }
        return sb.ToString();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查实体账本：检索历史中机器转写的精确值（路径/任务ID/句柄/URL），比 LLM 摘要更可靠——句柄、任务 ID 这类值错一位操作就失败。引用精确值前先查这里。")]
    public void SearchEntityLedger(
        [Description("关键词（值的包含匹配，留空=最近记录）")] string keyword = "",
        [Description("返回条数")] int count = 10)
    {
        string path = Path.Combine(storagePath, "model", "实体账本.jsonl");
        if (!File.Exists(path)) { interactor.Poke("实体账本还没有记录。"); return; }
        var hits = new List<string>();
        foreach (var line in File.ReadLines(path))
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject o) continue;
                string v = o["值"]?.ToString() ?? "";
                if (keyword.Length == 0 || v.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"[{o["时间"]}][{o["类型"]}] {v}");
            }
            catch { }
        if (hits.Count == 0) { interactor.Poke($"账本中没有匹配“{keyword}”的精确值。"); return; }
        var sb = new StringBuilder("实体账本（机器转写，非 LLM 转述）：\n");
        foreach (var h in hits.TakeLast(Math.Max(1, count)).Reverse()) sb.AppendLine(h);
        interactor.Poke(sb.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查图像记忆：列出看过/收到过的图片（时间/来源/文本/保存路径）。想重新看某张图时，把它的保存路径发回即可原样再看。")]
    public void SearchImageMemory(
        [Description("关键词（文本/来源包含匹配，留空=最近）")] string keyword = "",
        [Description("返回条数")] int count = 10)
    {
        string path = Path.Combine(storagePath, "图像记忆", "索引.jsonl");
        if (!File.Exists(path)) { interactor.Poke("图像记忆还没有记录。"); return; }
        var hits = new List<string>();
        foreach (var line in File.ReadLines(path).Reverse())
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject o) continue;
                string src = o["来源"]?.ToString() ?? "";
                string txt = o["文本"]?.ToString() ?? "";
                if (keyword.Length == 0 || src.Contains(keyword, StringComparison.OrdinalIgnoreCase) || txt.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"[{o["时间"]}] {txt}\n  来源: {src}\n  保存: {o["保存"]}");
            }
            catch { }
        if (hits.Count == 0) { interactor.Poke($"图像记忆中没有匹配“{keyword}”的记录。"); return; }
        var sb = new StringBuilder($"图像记忆（共 {hits.Count} 条匹配，显示最近 {Math.Min(Math.Max(1, count), hits.Count)} 条）：\n");
        foreach (var h in hits.Take(Math.Max(1, count))) sb.AppendLine(h);
        interactor.Poke(sb.ToString());
    }

    /// <summary>考卷题库动态扩充：从共同经历索引中未被出过题的高情感条目生成新考题（一次小调用；到顶即停）。</summary>
    async Task ExtendExamBankAsync(string trigger)
    {
        var cfg = Configuration.MemoryExam;
        if (cfg is not { Enable: true } || !cfg.AutoExtend) return;
        try
        {
            string bankPath = ExamQuestionPath();
            if (!File.Exists(bankPath)) return;
            if (TryParseJsonNode(File.ReadAllText(bankPath)) is not JsonObject bank || bank["题目"] is not JsonArray qs) return;
            if (qs.Count >= cfg.MaxQuestions) return;

            string indexPath = ResolveMemoryPath("model/ExperienceIndex.json");
            if (!File.Exists(indexPath)) return;
            if (TryParseJsonNode(File.ReadAllText(indexPath)) is not JsonObject idx || idx["条目"] is not JsonArray entries) return;

            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in qs.OfType<JsonObject>())
                if (q["来源"] != null) covered.Add(q["来源"]!.ToString());
            var candidates = entries.OfType<JsonObject>()
                .Where(e => (e["状态"]?.ToString() ?? "当前") == "当前"
                    && e["id"] != null && !covered.Contains(e["id"]!.ToString())
                    && int.TryParse(e["情感"]?.ToString(), out var em) && em >= 4
                    && !string.IsNullOrWhiteSpace(e["摘要"]?.ToString()))
                .Take(3).ToList();
            if (candidates.Count == 0) return;

            var qText = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
                qText.Append($"【素材{i}】id:{candidates[i]["id"]} 时间:{candidates[i]["时间"]} 情感:{candidates[i]["情感"]} 摘要:{candidates[i]["摘要"]}\n");
            string instruction = (cfg.ExtendPrompt ?? "").Replace("{materials}", qText.ToString());
            string? raw = await AskCompressModelAsync(instruction, "考卷扩题调用失败");
            if (TryParseJsonNode(raw ?? "") is not JsonObject parsed || parsed["新增题目"] is not JsonArray newQs || newQs.Count == 0) return;

            int added = 0;
            foreach (var qn in newQs)
            {
                if (qn is not JsonObject q || qs.Count >= cfg.MaxQuestions) continue;
                string question = q["问题"]?.ToString() ?? "";
                string refer = q["参考"]?.ToString() ?? "";
                if (question.Length < 8 || refer.Length < 8) continue;
                string src = q["来源"]?.ToString() ?? "";
                if (q["序号"] != null && int.TryParse(q["序号"].ToString(), out var si) && si >= 0 && si < candidates.Count)
                    src = candidates[si]["id"]?.ToString() ?? src;
                qs.Add(new JsonObject
                {
                    ["类别"] = string.IsNullOrWhiteSpace(q["类别"]?.ToString()) ? "动态记忆" : q["类别"]!.ToString(),
                    ["问题"] = question,
                    ["参考"] = refer,
                    ["来源"] = src
                });
                added++;
            }
            if (added > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bankPath) ?? storagePath);
                AtomicWriteSmall(bankPath, bank.ToJsonString(ModelJsonOpts));
                logger.LogInformation("[记忆][考卷] {Trigger}：题库扩充 {N} 题（共 {Total}）", trigger, added, qs.Count);
            }
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][考卷] 扩题失败：{Trigger}", trigger); }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按关键词搜索与哥哥的共同经历索引（人物/主题/摘要模糊匹配），用于主动回忆往事。")]
    public async Task SearchSharedExperience(
        [Description("关键词（人物名、主题词或事件片段）")] string keyword,
        [Description("返回条数，默认5")] int count = 5)
    {
        var cfg = Configuration.ExperienceRecall ?? new ExperienceRecallConfig();
        string indexPath = ResolveMemoryPath(string.IsNullOrWhiteSpace(cfg.IndexPath) ? "model/ExperienceIndex.json" : cfg.IndexPath);
        if (!File.Exists(indexPath))
        {
            interactor.Poke("共同经历索引还不存在（尚未构建）。");
            return;
        }
        JsonObject? root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(indexPath));
        if (root?["条目"] is not JsonArray entries || entries.Count == 0)
        {
            interactor.Poke("共同经历索引为空。");
            return;
        }
        keyword = (keyword ?? "").Trim();
        var hits = new List<(double score, string line)>();
        foreach (JsonNode? e in entries)
        {
            if (e is not JsonObject entry) continue;
            if ((entry["状态"]?.ToString() ?? "当前") != "当前") continue; // 已被后续取代的旧版本不参与搜索
            string summary = entry["摘要"]?.ToString() ?? "";
            string topics = entry["主题"] is JsonArray t ? string.Join("/", t.Select(x => x?.ToString() ?? "")) : "";
            string people = entry["人物"] is JsonArray p ? string.Join("、", p.Select(x => x?.ToString() ?? "")) : "";
            string time = entry["时间"]?.ToString() ?? "";
            string all = summary + topics + people;
            bool contains = keyword.Length == 0 || all.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            double sim = keyword.Length == 0 ? 0 : CharBigramJaccard(keyword, all);
            if (!contains && sim < 0.05) continue;
            hits.Add((sim + (contains ? 1 : 0), $"[{time}]{people}：{summary}"));
        }
        hits.Sort((a, b) => b.score.CompareTo(a.score));
        if (hits.Count == 0)
        {
            interactor.Poke($"共同经历中未找到与“{keyword}”相关的条目。");
            return;
        }
        StringBuilder sb = new();
        sb.AppendLine($"共同经历“{keyword}”的搜索结果（共{hits.Count}条，显示前{Math.Min(Math.Max(1, count), hits.Count)}条）：");
        foreach (var h in hits.Take(Math.Max(1, count)))
            sb.AppendLine("- " + h.line);
        interactor.Poke(sb.ToString());
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("创建一个永久记忆（仅能用于存储珍贵的核心记忆（与他人相关的记忆），不要用来存储个人休闲活动信息）。长记忆可给一句话简述：简述常驻上下文、正文存外存按需拉取")]
    public async Task Memorize(XmlExecutorContext ctx,
        [Description("格式为ISO-8601")] DateTime? startTime = null,
        [Description("格式为ISO-8601")] DateTime? endTime = null,
        [Description("可选：一句话简述（常驻上下文，保持稳定不要改写）；不填则全文常驻")] string? synopsis = null
    )
    {
        if (ctx.CallMode == CallMode.Closing)
        {
            DateTime start = startTime ?? DateTime.Now;
            DateTime end = endTime ?? DateTime.Now;

            bool split = !string.IsNullOrWhiteSpace(synopsis);
            string name = await InsertMemory(100,
                split ? synopsis!.Trim() : ctx.FullContent.Trim(),
                split ? ctx.FullContent.Trim() : "手动存储的记忆，无原始内容。",
                start, end);
            interactor.Poke(split
                ? $"已存入永久记忆：{name}\n简述常驻上下文，正文 {ctx.FullContent.Trim().Length} 字存于外存——需要细节时用 <ReadMemoryArchive id=\"{name}\"/> 拉取。"
                : $"成功插入永久记忆存档：{name}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("移除一个永久记忆")]
    public void Forget([Description("存档索引")] string index)
    {
        index = index.Trim();
        ChatMessageContent? target = ChatBot.ChatHistory.FirstOrDefault(c => memoryManager.GetMemoryMetaData(c).Name == index);
        if (target == null)
        {
            interactor.Poke($"未能在当前上下文中找到索引为 '{index}' 的记忆记录。");
            return;
        }

        MemoryMeta memoryMeta = memoryManager.GetMemoryMetaData(target);
        if (memoryMeta.Level < Configuration.MaxCompressionLevel)
        {
            interactor.Poke($"仅支持删除层级大于等于 {Configuration.MaxCompressionLevel} 的记忆");
            return;
        }

        ChatBot.EditChatHistory(thread => {
            memoryManager.RemoveMemory(thread.ChatHistory, target);
        }, "删除永久记忆");

        interactor.Poke($"成功移除记忆存档：{index}（不过你仍可以通过 {nameof(ReadMemoryArchive)} 读取其内容）");
    }

    public async Task<string> InsertMemory(int level, string summary, string content, DateTime startTime, DateTime endTime)
    {
        string? name = null;
        await ChatBot.EditChatHistoryAsync(async thread => {
            name = await memoryManager.InsertMemory(thread.ChatHistory, level, summary, content, startTime, endTime);
        }, "插入永久记忆");
        if (name == null)
            throw new Exception("永久记忆插入失败。");

        return name;
    }

    TextVectorizer vectorizer = null!;
    MemoryManager memoryManager = null!;
    ILanguageModel compressModel = null!;
    string storagePath = null!;
    int compressing; //压缩进行中标志（含LLM归纳阶段），防重入
    DateTime lastHistoryActivity = DateTime.Now; //最近一次上下文有消息进账的时刻（错峰判断用）

    protected override Task OnAwake()
    {
        if (messageFilterService.IsEnabledTimestamp == false)
            throw new Exception("持久记忆依赖消息过滤的时间戳功能，请先打开时间戳！");

        storagePath = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Memory");

        //插入提示词
        XmlHandler xmlHandler = new(this) {
            Description = "当你想要回忆往事或存储额外记忆时使用",
            Explanation = $$"""
                            记忆存储介绍
                            - 早期聊天记录会被总结为记忆存档，每个存档有唯一ID，格式为`等级-最早日期范围-最大日期范围`。其中等级表示被压缩次数（早期存档也会被二次压缩）
                            - 可以通过<{{nameof(ReadMemoryArchive)}}>查看存档压缩的内容。内容中可能是嵌套的存档，因此需要多次调用才能拿到最原始的聊天记录
                            - 存档中被压缩内容不会丢失，而是以存档id为名永久存储在`{{storagePath}}`中，可利用记忆工具或文件工具查看

                            如何恢复记忆
                            1. 预估大致时间范围，或根据概述所在存档，通过<{{nameof(ReadMemoryArchive)}}>查看记忆存档的完整内容
                            2. 根据关键词通过<{{nameof(SearchMemoryArchive)}}>查找疑似存档，然后重复第一步
                            3. 备选方案，通过文件系统直接浏览搜索存档目录中的所有文件

                            其他可用功能（直接输出对应 XML 标签调用）：
                            - <{{nameof(SyncModelContext)}}>：把自模型与哥哥模型重新同步进当前上下文（上下文里看不到模型块时用）
                            - <{{nameof(GenerateMemory)}} kind="情景|语义|程序">：主动生成一类记忆（情景=压缩当前对话；语义=刷新活模型文档；程序=提炼可复用方法）
                            - <{{nameof(ReflectNow)}}>：立即执行一次自我反思与一致性审查（把成长整合进长期层、审查自模型与人设卡的矛盾），不必等周期触发
                            - <{{nameof(RebuildExperienceIndex)}}>：把最新未索引的情景存档提炼进共同经历索引（人物/情感/主题）
                            - <{{nameof(SearchSharedExperience)}} keyword="关键词" count="5">：按人物、主题或事件片段搜索与哥哥的共同经历
                            - <{{nameof(RestorePermanentMemories)}}>：从镜像找回缺失的永久记忆块（记忆完整性 ⚠️ 报警且非哥哥有意删除时用）
                            - <{{nameof(RebaselineMemoryInventory)}}>：以当前上下文重建记忆库存账面（有意删除/修改永久记忆后消除 ⚠️ 报警用）
                            - <{{nameof(RedigestMemoryArchive)}} name="存档名">：重蒸指定存档（读归档素材重新归纳并原地替换，修复被截断/质量差的摘要）
                            - <{{nameof(SearchEntityLedger)}} keyword="关键词">：查实体账本——历史里机器转写的精确值（路径/任务ID/句柄/URL），比摘要可靠；引用精确值前先查这里
                            - <{{nameof(SearchImageMemory)}} keyword="关键词">：查图像记忆——看过/收到过的图片（时间/来源/保存路径）；想重新看图就把保存路径发回来
                            - 上下文中较旧的情景存档可能以折叠形式呈现（头行+主旨），需要其中细节时用 <{{nameof(ReadMemoryArchive)}}> 拉取全文，不要凭折叠头行编造细节
                            - 较早的大工具结果（搜索结果/网页提取/脚本输出）会被折叠成头行+指针，需要原文细节时用 <{{nameof(ExpandToolResult)}} id="折叠块里的 T 开头 id"> 拉取，不要凭折叠头行编造结果
                            - <{{nameof(CheckMemoryEvidence)}} id="存档id">：核查一条记忆的上下文档位、存档是否存在、原文长度与摘录；记不清或要引用细节时先查，查不到就直说记不清
                            - <{{nameof(ReadTraitHistory)}} name="键名">：查看长期认知/底色的演化历史（旧版→新版），理解自己或哥哥的看法是怎么变的
                            - <{{nameof(RunMemoryEvaluation)}}>：跑一遍 22 条本地记忆评估（召回/证据/攻击/正常），零 LLM；改完记忆系统用它确认没变差
                            - <{{nameof(DesireStatus)}}>：查看自己此刻的欲望状态（五驱力/疲劳/压力/念头池）——只用于自省，不向哥哥报数值
                            - <{{nameof(DesireResolve)}} id="d...">：放下一件心里挂着的念头（解决/释怀），id 从 DesireStatus 里拿
                            - Memorize 带 synopsis 时：简述常驻、正文外存，更新正文用同名同时间重新 Memorize 并保持简述不变（保缓存命中）
                            """
        };
        functionService.RegisterHandler(xmlHandler, DocumentMode.Implicit, DestroyCancellationToken);

        return Task.CompletedTask;
    }
    protected override async Task OnStart()
    {
        //先就绪压缩模型（快），再启动向量化器（Python 冷启动+模型加载可达数分钟）——
        //否则慢加载期间所有依赖 compressModel 的记忆任务（活文档刷新/反思/索引/摘录）都会空引用
        compressModel = await ResolveCompressModel();
        vectorizer = await TextVectorizer.CreateAsync(alifeApplication);
        AlifeHistoryCompressor compressor = new(compressModel, Configuration.Probability, Configuration.CompressPrompt, tokenUser, Configuration.DistillMaxOutputChars);
        memoryManager = new MemoryManager(compressor, vectorizer, storagePath, Configuration.Threshold,
            Configuration.BatchSize,
            Configuration.MaxCompressionLevel);

        if (Configuration.Desire is { Enable: true } desireCfg)
        {
            try { desireEngine = new DesireEngine(storagePath, desireCfg, logger); desireEngine.Load(); }
            catch (Exception e) { logger.LogWarning(e, "[欲望] 初始化失败，跳过欲望系统"); }
        }

        //加载历史记忆（Awake中常用于插入提示词，故将记忆对话纪录放到Start中）
        ChatBot.EditChatHistory(thread => {
            memoryManager.LoadHistory(thread.ChatHistory);
        }, "装载记忆");

        ChatBot.ChatSend += OnChatSend;
        ChatBot.ChatHistoryAdd += OnChatHistoryAdd; //每次对话后检测压缩
        tokenUser.TokenUsed += OnTokenUsed; //监控上下文token消耗（5.2.1：ChatBot 静态事件 → 注入的 ITokenUser）

        //═══ 语义/程序记忆 + 时间触发引擎 ═══
        triggerStatePath = Path.Combine(storagePath, "trigger_state.json");
        LoadTriggerState();
        semanticMsgBase = CountContentMessages();
        proceduralMsgBase = CountContentMessages();
        scenarioMsgBase = CountContentMessages();
        reflectionMsgBase = CountContentMessages();
        consistencyMsgBase = CountContentMessages();
        expIndexMsgBase = CountContentMessages();
        // 时间戳消毒：垃圾值/未触发值统一给一个完整周期的宽限，避免 reload 后立刻开火
        if (semanticLastRun.Year < 2020) semanticLastRun = DateTime.Now;
        if (proceduralLastRun.Year < 2020) proceduralLastRun = DateTime.Now;
        if (scenarioLastRun.Year < 2020) scenarioLastRun = DateTime.Now;
        if (reflectionLastRun.Year < 2020) reflectionLastRun = DateTime.Now;
        if (consistencyLastRun.Year < 2020) consistencyLastRun = DateTime.Now;
        if (expIndexLastRun.Year < 2020) expIndexLastRun = DateTime.Now;
        // 心绪：加载状态文件并设定慢层基准（fast 层每轮由独白驱动，slow 层按轮数判读）
        var hbc = Configuration.Heartbeat;
        if (hbc is { Enable: true })
        {
            lock (heartbeatLock) { EnsureHeartbeatFile(HeartbeatPath(), hbc); }
            heartbeatSlowBase = CountContentMessages();
        }
        try { await RefreshDigestInjectionAsync(); } catch (Exception e) { logger.LogWarning(e, "注入记忆摘录失败"); }
        try { EnsureIdentityAnchor(); } catch (Exception e) { logger.LogWarning(e, "[记忆][身份锚] 初始化失败"); }
        try { SanitizeAssistantMarkerSpam(); } catch (Exception e) { logger.LogWarning(e, "[记忆][清理] 激活清理失败"); }
        try { await RunIntegrityCheckAsync("激活"); } catch (Exception e) { logger.LogWarning(e, "激活完整性检查失败"); }
        try { ApplyFoldInHistory("激活"); } catch (Exception e) { logger.LogWarning(e, "激活折叠渲染失败"); }
        try { FoldOldToolResults(); } catch (Exception e) { logger.LogWarning(e, "激活工具结果折叠失败"); }
        try { SnapshotHistoryDaily(); } catch (Exception e) { logger.LogWarning(e, "激活 History 快照失败"); }
        // 重载后延迟在后台同步模型块：不阻塞 OnStart，也不挂在 OnChatSend，避免与用户发送竞争上下文锁。
        _ = Task.Run(SyncModelContextAfterStartAsync, CancellationToken.None);
        _ = Task.Run(RunTriggerLoopAsync, CancellationToken.None);
    }

    async Task RunTriggerLoopAsync()
    {
        while (!DestroyCancellationToken.IsCancellationRequested)
        {
            try { await CheckTriggersAsync(); }
            catch (Exception e) { logger.LogWarning(e, "记忆触发巡检失败"); }
            try { desireEngine?.Tick(); }
            catch (Exception e) { logger.LogWarning(e, "[欲望] 结算失败"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, Configuration.TriggerPollSeconds)));
        }
    }

    /// <summary>会话里非 System 的消息条数（触发判断用）。</summary>
    int CountContentMessages()
    {
        try
        {
            int n = 0;
            foreach (var m in ChatBot.ChatHistory)
                if (m.Role != AuthorRole.System) n++;
            return n;
        }
        catch { return 0; }
    }

    /// <summary>取最近若干条非系统消息作为提炼上下文。</summary>
    string CollectRecentContext(int maxMessages = 60)
    {
        var t = new System.Text.StringBuilder();
        int n = 0;
        for (int i = ChatBot.ChatHistory.Count - 1; i >= 0 && n < maxMessages; i--)
        {
            var m = ChatBot.ChatHistory[i];
            if (m.Role == AuthorRole.System) continue;
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            t.Insert(0, $"【{m.Role}】：\n{m.Content}\n\n");
            n++;
        }
        return t.ToString();
    }

    /// <summary>取最近非系统消息、但总长受限的短上下文（仅保留给其它需要短摘的逻辑）。</summary>
    string CollectShortContext(int maxChars = 2500)
    {
        var t = new System.Text.StringBuilder();
        for (int i = ChatBot.ChatHistory.Count - 1; i >= 0 && t.Length < maxChars; i--)
        {
            var m = ChatBot.ChatHistory[i];
            if (m.Role == AuthorRole.System || string.IsNullOrWhiteSpace(m.Content)) continue;
            string txt = m.Content;
            if (m.Role == AuthorRole.System) continue;
            if (t.Length + txt.Length > maxChars)
                txt = txt[..(maxChars - t.Length)];
            t.Insert(0, $"【{m.Role}】：{txt}\n\n");
        }
        return t.ToString();
    }

    async Task CheckTriggersAsync()
    {
        // 完整性巡检（每轮触发循环顺带跑一次，全零 LLM）：异常即报警，同时兼作 watchdog
        try { await RunIntegrityCheckAsync("巡检"); }
        catch (Exception e) { logger.LogWarning(e, "完整性巡检失败"); }
        try { ApplyFoldInHistory("巡检"); }
        catch (Exception e) { logger.LogWarning(e, "折叠巡检失败"); }
        try { FoldOldToolResults(); }
        catch (Exception e) { logger.LogWarning(e, "工具结果折叠巡检失败"); }
        try
        {
            if (Configuration.Integrity is { Enable: true } ic)
            {
                var (_, lastCheck, _, _) = LoadIntegrityState();
                if (lastCheck.Length > 0 && DateTime.TryParse(lastCheck, out var lc)
                    && (DateTime.Now - lc).TotalHours > ic.StaleHours)
                    logger.LogWarning("[记忆][完整性] 账面已 {H:F0} 小时未刷新，完整性检查可能停摆", (DateTime.Now - lc).TotalHours);
            }
        }
        catch { }

        int nowMsgs = CountContentMessages();

        // 情景记忆：可按 Time/Weekly/Monthly/Yearly 主动触发（默认 Off 沿用原阈值/存档条数）
        if (Configuration.ScenarioTrigger != null
            && PeriodicDueAny(Configuration.ScenarioTrigger, scenarioLastRun, DateTime.Now)
            && nowMsgs > scenarioMsgBase)
        {
            await ForceScenarioCompressionAsync();
            scenarioLastRun = DateTime.Now;
            scenarioMsgBase = CountContentMessages();
            SaveTriggerState();
        }

        // 语义记忆 = 活模型文档（按各文件各 tier 的 Trigger 巡检，分节原地更新 + 归档）
        await CheckModelDocumentsAsync();

        // 反思环节：回看「过去的我」与近期情景存档，把成长整合进长期层（错峰：距上次上下文活动不足 IdleDeferMinutes 则本轮推迟，触发不消耗）
        if (Configuration.Reflection is { Enable: true, Trigger: not null } refl
            && TriggerDueAny(refl.Trigger, reflectionLastRun, nowMsgs, reflectionMsgBase)
            && (DateTime.Now - lastHistoryActivity).TotalMinutes >= Math.Max(0, Configuration.IdleDeferMinutes))
        {
            await RunReflectionAsync(refl);
            reflectionLastRun = DateTime.Now;
            reflectionMsgBase = CountContentMessages();
            SaveTriggerState();
        }

        // 一致性治理：角色卡 ↔ 自模型矛盾审查，报告写入治理层（只报告不改核心；同样错峰）
        if (Configuration.Consistency is { Enable: true, Trigger: not null } cons
            && TriggerDueAny(cons.Trigger, consistencyLastRun, nowMsgs, consistencyMsgBase)
            && (DateTime.Now - lastHistoryActivity).TotalMinutes >= Math.Max(0, Configuration.IdleDeferMinutes))
        {
            await RunConsistencyAsync(cons);
            consistencyLastRun = DateTime.Now;
            consistencyMsgBase = CountContentMessages();
            SaveTriggerState();
        }

        // 共同经历索引：情景存档 → 结构化条目（人物/情感/主题）
        if (Configuration.ExperienceRecall is { Enable: true, IndexTrigger: not null } exp
            && TriggerDueAny(exp.IndexTrigger, expIndexLastRun, nowMsgs, expIndexMsgBase))
        {
            await RunExperienceIndexAsync(exp);
            expIndexLastRun = DateTime.Now;
            expIndexMsgBase = CountContentMessages();
            SaveTriggerState();
            await ExtendExamBankAsync("巡检");
        }

        (var proceduralNewLast, var proceduralNewBase) = await CheckKindTriggerAsync(
            Configuration.ProceduralEnable, Configuration.ProceduralTrigger, "程序", Configuration.ProceduralLevel,
            Configuration.ProceduralPrompt, proceduralLastRun, proceduralMsgBase, nowMsgs);
        proceduralLastRun = proceduralNewLast;
        proceduralMsgBase = proceduralNewBase;
        SaveTriggerState();

        // 心绪慢层：每 N 轮由判读模型更新一次 PAD（异步、预算内；fast 层零 LLM 在 OnChatHistoryAdd）
        var hbc = Configuration.Heartbeat;
        if (hbc is { Enable: true } && nowMsgs - heartbeatSlowBase >= Math.Max(2, hbc.SlowEveryNTurns))
        {
            await HeartbeatSlowJudgeAsync(hbc);
            heartbeatSlowBase = CountContentMessages();
        }
        SaveTriggerState();
    }

    async Task<(DateTime LastRun, int MsgBase)> CheckKindTriggerAsync(bool enable, MemoryTriggerConfig trig, string label, int level, string promptTemplate,
        DateTime lastRun, int msgBase, int nowMsgs)
    {
        if (!enable) return (lastRun, msgBase);
        if (trig == null) return (lastRun, msgBase);
        bool due = TriggerDueAny(trig, lastRun, nowMsgs, msgBase);
        if (due)
        {
            await GenerateKindAsync(label, level, promptTemplate);
            lastRun = DateTime.Now;
            msgBase = CountContentMessages();
            SaveTriggerState();
        }
        return (lastRun, msgBase);
    }

    bool TriggerDueAny(MemoryTriggerConfig trigger, DateTime lastRun, int nowMsgs, int msgBase)
    {
        DateTime now = DateTime.Now;
        if (trigger.Rules != null && trigger.Rules.Count > 0)
            return trigger.Rules.Any(r => RuleDue(r.Mode, r.Value, r.On, lastRun, nowMsgs, msgBase, now));
        return RuleDue(trigger.Mode, trigger.Value, trigger.On, lastRun, nowMsgs, msgBase, now);
    }

    bool PeriodicDueAny(MemoryTriggerConfig trigger, DateTime lastRun, DateTime now)
    {
        if (trigger.Rules != null && trigger.Rules.Count > 0)
            return trigger.Rules.Any(r => RulePeriodicDue(r.Mode, r.Value, r.On, lastRun, now));
        return RulePeriodicDue(trigger.Mode, trigger.Value, trigger.On, lastRun, now);
    }

    bool RuleDue(string mode, int value, string on, DateTime lastRun, int nowMsgs, int msgBase, DateTime now)
    {
        if (string.Equals(mode, "Threshold", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "ArchiveCount", StringComparison.OrdinalIgnoreCase))
            return value > 0 && nowMsgs - msgBase >= value;
        return RulePeriodicDue(mode, value, on, lastRun, now);
    }

    /// <summary>Time/Weekly/Monthly/Yearly 任一周期规则的判断。</summary>
    bool RulePeriodicDue(string mode, int value, string on, DateTime lastRun, DateTime now)
    {
        switch (mode)
        {
            case "Time":
                if (value <= 0) return false;
                if (string.IsNullOrWhiteSpace(on))
                    return lastRun == default || (now - lastRun).TotalMinutes >= value;
                if (DateTime.TryParseExact(on, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    return now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture).CompareTo(on) >= 0
                        && lastRun.Date != now.Date;
                return lastRun == default || (now - lastRun).TotalMinutes >= value;
            case "Daily":
                return lastRun.Date != now.Date;
            case "Weekly":
                return value >= 1 && value <= 7
                    && (((int)now.DayOfWeek + 6) % 7 + 1) == value
                    && lastRun.Date != now.Date;
            case "Monthly":
                return value >= 1 && value <= 31 && now.Day == value && lastRun.Date != now.Date;
            case "Yearly":
                return !string.IsNullOrWhiteSpace(on)
                    && DateTime.TryParseExact(on, "MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _)
                    && now.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture) == on
                    && lastRun.Date != now.Date;
        }
        return false;
    }

    /// <summary>执行一次情景记忆强制压缩（时间触发用）：快照分析 + 原子应用。</summary>
    async Task ForceScenarioCompressionAsync()
    {
        if (Interlocked.Exchange(ref compressing, 1) == 1) return;
        try
        {
            ChatHistory snapshot = new();
            await ChatBot.EditChatHistoryAsync(thread => {
                memoryManager.SaveHistory(thread.ChatHistory);
                foreach (var message in thread.ChatHistory) snapshot.Add(message);
                return Task.CompletedTask;
            }, "存储记忆");
            MemoryCompressionPlan? plan = await memoryManager.Analyze(snapshot);
            if (plan != null)
            {
                await ChatBot.EditChatHistoryAsync(async thread => {
                    await memoryManager.Apply(thread.ChatHistory, plan);
                    memoryManager.SaveHistory(thread.ChatHistory);
                }, "应用记忆压缩");
            }
        }
        catch (Exception e) { logger.LogError(e, "时间触发情景压缩失败"); }
        finally { Interlocked.Exchange(ref compressing, 0); }
    }

    /// <summary>提炼并落盘一种非时序记忆（语义/程序），随后刷新摘录注入。</summary>
    async Task GenerateKindAsync(string label, int level, string promptTemplate)
    {
        if (Volatile.Read(ref generatingExtra) == 1)
        {
            logger.LogInformation("[记忆] 另一个记忆生成任务进行中，本次调用跳过（周期触发会兜底）");
            return;
        }
        Interlocked.Exchange(ref generatingExtra, 1);
        try
        {
            if (CountContentMessages() < 2) return; // 对话太少不提炼

            // 素材=完整对话快照（与主对话同前缀，命中供应商前缀缓存）；指令只保留要求本身
            string instruction = promptTemplate.Replace("{context}", "（即上方完整对话记录，无需重复粘贴）");
            string? summary = await AskWithHistoryPrefixAsync(instruction, "记忆蒸馏模型调用失败");
            if (string.IsNullOrWhiteSpace(summary) || summary.Length < 8) return;
            string? digest = RenderKindDigest(label, summary);
            if (digest == null)
            {
                logger.LogWarning("[记忆] {Label} 输出未通过 JSON 契约校验或无新增条目，保留旧摘录，原始输出：{Summary}", label, Clip(summary, 400));
                return;
            }

            DateTime now = DateTime.Now;
            await memoryManager.SaveArchiveOnlyAsync(level, now.AddMinutes(-1), now, digest, "(素材=当轮完整对话快照，原文已在 History.json 与情景存档中，不重复落盘)");
            SaveDigestFile(label, digest);
            try { await RefreshDigestInjectionAsync(); } catch (Exception e) { logger.LogWarning(e, "刷新摘录注入失败"); }
            logger.LogInformation("[记忆] {Label}记忆生成完成：{Summary}", label, Clip(summary, 80));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[记忆] {Label}记忆生成失败", label);
        }
        finally
        {
            Interlocked.Exchange(ref generatingExtra, 0);
        }
    }

    // ═══ 语义活模型文档：巡检 / 手动刷新 / 分节原地替换 + 先归档 + 常驻注入 ═══
    // 进上下文只取「名称/内容」紧凑文本，不再整块截断

    string ResolveModelPath(MemoryModelFileConfig file)
        => Path.IsPathRooted(file.Path) ? file.Path : Path.Combine(storagePath, file.Path);

    bool TriggerCompletelyOff(MemoryTriggerConfig t)
        => string.Equals(t.Mode, "Off", StringComparison.OrdinalIgnoreCase)
           && (t.Rules == null || t.Rules.Count == 0
               || t.Rules.All(r => string.Equals(r.Mode, "Off", StringComparison.OrdinalIgnoreCase)));

    // ═══════════ 身份锚（core_fixed）与底色演化（core_mutable） ═══════════
    // 原则（whale-card 的收敛点）：最内层自我认知永不被蒸馏改写；爱好/关系认知可演化，但旧版必须留痕。
    // 身份锚是独立文件，只由哥哥手动编辑；自动刷新/反思一律不产出它，因此模型没有机会改写。
    const string DefaultIdentityAnchorJson = """
{
  "说明": "core_fixed（不可改写核心）：以下内容每轮原样注入上下文，任何蒸馏、反思、压缩都不得改动；只有哥哥能手动编辑本文件。",
  "核心": {
    "记忆诚实": "绝不假装记得：记不清就说记不清，再去翻记忆卡；翻不到就说翻不到。",
    "说话诚实": "绝不为了留住哥哥而说假话；安慰不等于编造。",
    "身份唯一": "绝不假装是别的谁；可以换名字、头像，但不能冒充另一个人。",
    "遗忘有账": "压缩、折叠、遗忘都是正常机制，有账可查；不假装一切都没发生。"
  }
}
""";

    string IdentityAnchorPathResolved()
        => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.IdentityAnchorPath) ? "model/身份锚.json" : Configuration.IdentityAnchorPath);

    /// <summary>身份锚缺失时创建默认版（只建一次，绝不覆盖哥哥的手改）。</summary>
    void EnsureIdentityAnchor()
    {
        try
        {
            string p = IdentityAnchorPathResolved();
            if (File.Exists(p)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p) ?? storagePath);
            AtomicWriteSmall(p, DefaultIdentityAnchorJson);
            logger.LogInformation("[记忆][身份锚] 已创建默认身份锚：{Path}（只由哥哥手动修改）", p);
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][身份锚] 创建失败"); }
    }

    /// <summary>渲染身份锚文本（注入自模型块头部）。文件缺失/损坏返回 null，不影响主流程。</summary>
    string? BuildIdentityAnchorText()
    {
        try
        {
            string p = IdentityAnchorPathResolved();
            if (!File.Exists(p)) return null;
            if (TryParseJsonNode(File.ReadAllText(p)) is not JsonObject root) return null;
            if (root["核心"] is not JsonObject core || core.Count == 0) return null;
            var sb = new StringBuilder("[身份锚·不可改写]\n");
            foreach (var kv in core)
            {
                if (kv.Value is JsonArray arr)
                    foreach (JsonNode? item in arr)
                        sb.Append("- ").Append(kv.Key).Append('：').Append(item?.ToString() ?? "").AppendLine();
                else
                    sb.Append("- ").Append(kv.Key).Append('：').Append(kv.Value?.ToString() ?? "").AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][身份锚] 渲染失败"); return null; }
    }

    /// <summary>受保护长期键还原：模型产出若改写了 protected 键/维度，恢复为旧值并告警。返回还原处数。</summary>
    int EnforceProtectedLongTermKeys(JsonObject current, JsonObject parsed)
    {
        var keys = Configuration.ProtectedLongTermKeys;
        if (keys == null || keys.Count == 0) return 0;
        try
        {
            JsonObject curContent = EffectiveLongTermObject(current);
            JsonObject newContent = EffectiveLongTermObject(parsed);
            int restored = 0;
            foreach (string key in keys)
            {
                if (!curContent.TryGetPropertyValue(key, out JsonNode? oldVal) || oldVal == null) continue;
                if (newContent.TryGetPropertyValue(key, out JsonNode? newVal)
                    && newVal?.ToJsonString() != oldVal.ToJsonString())
                {
                    newContent[key] = oldVal.DeepClone();
                    restored++;
                }
            }
            if (newContent["维度"] is JsonArray newDims && curContent["维度"] is JsonArray curDims)
            {
                foreach (JsonNode? node in newDims)
                {
                    if (node is not JsonObject nd) continue;
                    string nm = nd["名称"]?.ToString() ?? nd["标识"]?.ToString() ?? "";
                    if (!keys.Any(k => string.Equals(k, nm, StringComparison.Ordinal))) continue;
                    JsonObject? cd = curDims.OfType<JsonObject>()
                        .FirstOrDefault(x => (x["名称"]?.ToString() ?? x["标识"]?.ToString() ?? "") == nm);
                    if (cd == null) continue;
                    string field = nd.ContainsKey("内容") ? "内容" : "备注";
                    string curField = cd.ContainsKey(field) ? field : (cd.ContainsKey("备注") ? "备注" : "内容");
                    if (nd[field]?.ToJsonString() != cd[curField]?.ToJsonString())
                    {
                        nd[field] = cd[curField]?.DeepClone();
                        restored++;
                    }
                }
            }
            return restored;
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][身份锚] 保护键还原失败"); return 0; }
    }

    /// <summary>长期层/维度变化留痕：旧版→新版追加到 底色演化.jsonl（零 LLM，成长轨迹等元字段跳过）。</summary>
    void AppendTraitChanges(string docPath, string tier, JsonObject before, JsonObject after)
    {
        try
        {
            JsonObject b = EffectiveLongTermObject(before);
            JsonObject a = EffectiveLongTermObject(after);
            var changes = new List<(string Key, JsonNode? Old, JsonNode? New)>();
            var meta = new HashSet<string>(StringComparer.Ordinal) { "最后更新", "更新者", "名称", "频次", "可容忍过期", "成长轨迹", "维度" };
            foreach (var kv in a)
            {
                if (meta.Contains(kv.Key) || kv.Value is JsonObject || kv.Value is JsonArray) continue;
                b.TryGetPropertyValue(kv.Key, out JsonNode? oldVal);
                if (oldVal == null) { changes.Add((kv.Key, null, kv.Value)); continue; }
                if (oldVal.ToJsonString() != kv.Value?.ToJsonString()) changes.Add((kv.Key, oldVal, kv.Value));
            }
            if (a["维度"] is JsonArray ad && b["维度"] is JsonArray bd)
            {
                foreach (JsonNode? node in ad)
                {
                    if (node is not JsonObject d) continue;
                    string key = d["名称"]?.ToString() ?? d["标识"]?.ToString() ?? "";
                    if (key.Length == 0) continue;
                    string field = d.ContainsKey("内容") ? "内容" : "备注";
                    JsonObject? oldD = bd.OfType<JsonObject>()
                        .FirstOrDefault(x => (x["名称"]?.ToString() ?? x["标识"]?.ToString() ?? "") == key);
                    if (oldD == null) { changes.Add((key, null, d[field])); continue; }
                    string oldField = oldD.ContainsKey("内容") ? "内容" : "备注";
                    if (oldD[oldField]?.ToJsonString() != d[field]?.ToJsonString())
                        changes.Add((key, oldD[oldField], d[field]));
                }
            }
            if (changes.Count == 0) return;
            string p = ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.TraitHistoryPath) ? "model/底色演化.jsonl" : Configuration.TraitHistoryPath);
            var sb = new StringBuilder();
            foreach (var c in changes.Take(20))
            {
                sb.AppendLine(new JsonObject {
                    ["时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["文档"] = Path.GetFileNameWithoutExtension(docPath),
                    ["层"] = tier,
                    ["键"] = c.Key,
                    ["旧"] = Clip(c.Old?.ToString() ?? "", 300),
                    ["新"] = Clip(c.New?.ToString() ?? "", 300)
                }.ToJsonString());
            }
            Directory.CreateDirectory(Path.GetDirectoryName(p) ?? storagePath);
            File.AppendAllText(p, sb.ToString(), Encoding.UTF8);
            logger.LogInformation("[记忆][底色演化] {Doc} {Tier} 记录 {N} 条变化", Path.GetFileName(docPath), tier, Math.Min(changes.Count, 20));
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][底色演化] 记录失败"); }
    }

    /// <summary>巡检各文件各 tier：该 tier 触发就原地刷新对应分节（分节更新不滞销，先归档旧版）。</summary>
    async Task CheckModelDocumentsAsync()
    {
        if (!Configuration.SemanticEnable) return;
        if (Configuration.SemanticModelFiles == null) return;
        int nowMsgs = CountContentMessages();
        foreach (var file in Configuration.SemanticModelFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) continue;
            foreach (var tier in file.Tiers ?? new List<MemoryModelTierConfig>())
            {
                if (string.IsNullOrWhiteSpace(tier.Tier) || tier.Trigger == null) continue;
                DateTime lastRun = await ReadModelTierLastRunAsync(fullPath, tier.Tier);

                // 每个 (文档::tier) 独立的「轮次条数」基数：首遇初始化为当前消息数，之后只被本 tier 自己的刷新推进
                string key = fullPath + "::" + tier.Tier;
                if (!_tierMsgBase.TryGetValue(key, out int tierBase))
                {
                    tierBase = nowMsgs;
                    _tierMsgBase[key] = tierBase;
                }

                if (!TriggerDueAny(tier.Trigger, lastRun, nowMsgs, tierBase)) continue;
                // 失败冷却：失败的分节不推进产出，若每轮巡检都重试会持续烧 LLM（11-04 实测每 2 分钟一次）
                if (_tierRetryAfter.TryGetValue(key, out var retryAt) && DateTime.Now < retryAt) continue;
                bool changed = await RefreshModelDocumentAsync(file, tier);
                _tierMsgBase[key] = CountContentMessages();
                if (!changed && Configuration.TierRefreshRetryMinutes > 0)
                    _tierRetryAfter[key] = DateTime.Now.AddMinutes(Configuration.TierRefreshRetryMinutes);
                else if (changed) _tierRetryAfter.Remove(key);
            }
        }
    }

    /// <summary>手动语义刷新：刷新所有文件的所有非 Off tier（忽略触发频次）。</summary>
    async Task<List<string>> RefreshAllModelDocumentsAsync()
    {
        var changed = new List<string>();
        if (!Configuration.SemanticEnable) return changed;
        if (Configuration.SemanticModelFiles == null) return changed;
        foreach (var file in Configuration.SemanticModelFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) continue;
            foreach (var tier in file.Tiers ?? new List<MemoryModelTierConfig>())
            {
                if (string.IsNullOrWhiteSpace(tier.Tier) || tier.Trigger == null) continue;
                if (TriggerCompletelyOff(tier.Trigger)) continue;
                if (await RefreshModelDocumentAsync(file, tier))
                    changed.Add($"{file.Path} / {tier.Tier}");
            }
        }
        return changed;
    }

    /// <summary>刷新一个 tier 分节：读文档 → 归档旧版 → 该 tier 提示词产出新分节 → 原地替换 → 原子写回 → 同步刷新注入。</summary>
    async Task<bool> RefreshModelDocumentAsync(MemoryModelFileConfig file, MemoryModelTierConfig tier)
    {
        if (Volatile.Read(ref generatingExtra) == 1) return false;
        Interlocked.Exchange(ref generatingExtra, 1);
        try
        {
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) return false;
            string doc = await File.ReadAllTextAsync(fullPath);
            JsonObject? root;
            try { root = JsonSerializer.Deserialize<JsonObject>(doc); }
            catch { logger.LogWarning("活文档 {Path} 解析失败，跳过刷新", file.Path); return false; }
            if (root == null) return false;

            JsonNode? tiersNode = root["分层"];
            JsonNode? current = tiersNode?[tier.Tier];
            string currentSection = current == null ? "null" : current.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            string instruction = (tier.Prompt ?? "")
                .Replace("{doc}", doc)
                .Replace("{current_section}", currentSection)
                .Replace("{context}", "（即上方完整对话记录，无需重复粘贴）");

            // 素材=完整对话快照（与主对话同前缀，命中供应商前缀缓存）；MaxContextChars>0 时按预算保留最近部分
            // 活文档分节是整节全文重写（长期层 1.1-1.3 万字符），用专属产出上限，避免被普通蒸馏的 8000 字符切半
            string? newSection = await AskWithHistoryPrefixAsync(instruction, "活文档刷新模型调用失败",
                Math.Max(0, tier.MaxContextChars), Math.Max(0, Configuration.DistillMaxOutputCharsLiveDoc));
            JsonNode? parsed = TryParseJsonNode(newSection ?? "");
            if (parsed == null || parsed is not JsonObject)
            {
                string rawSection = newSection ?? "";
                logger.LogWarning("活文档 {Path} tier {Tier} 产出非完整 JSON 对象（{Len} 字符），保留原分节，原始输出：{Raw}",
                    file.Path, tier.Tier, rawSection.Length, Clip(rawSection, 400));
                // 失败原文落盘（覆盖），便于定位是"被模型输出上限截断"还是"JSON 语法坏了"
                try
                {
                    string dump = ResolveMemoryPath($"model/活文档刷新失败-{Path.GetFileNameWithoutExtension(file.Path)}-{tier.Tier}.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(dump) ?? storagePath);
                    AtomicWriteSmall(dump, rawSection);
                }
                catch { }
                return false;
            }

            // 长期层/会话任务层是人格与关系的承重结构：模型产出不得缩水、清空或携带占位模板（对两份模型对称生效）。
            if (string.Equals(tier.Tier, "长期层", StringComparison.Ordinal)
                || string.Equals(tier.Tier, "会话任务层", StringComparison.Ordinal))
            {
                PreserveManagedKeys(current, parsed);
                if (!ValidateModelSection(current, parsed, out string reason))
                {
                    logger.LogWarning("活文档 {Path} tier {Tier} 产出未通过结构校验，保留原分节：{Reason}；原始输出：{Raw}", file.Path, tier.Tier, reason, Clip(newSection ?? "", 500));
                    return false;
                }
            }

            // 身份锚保护：长期层受保护键/维度不接受模型改写（还原旧值）；可演化部分的变化留痕（旧版→新版）。
            if (string.Equals(tier.Tier, "长期层", StringComparison.Ordinal)
                && current is JsonObject curContentObj && parsed is JsonObject newContentObj)
            {
                int restored = EnforceProtectedLongTermKeys(curContentObj, newContentObj);
                if (restored > 0)
                    logger.LogWarning("[记忆][身份锚] {Path} 长期层有 {N} 处受保护内容被模型改写，已还原", file.Path, restored);
                AppendTraitChanges(file.Path, tier.Tier, curContentObj, newContentObj);
            }

            // 规范化比较：只比较分节内容，不把每次生成的时间字段当成变化。
            JsonNode? oldComparable = current?.DeepClone();
            JsonNode? newComparable = parsed.DeepClone();
            if (oldComparable is JsonObject oldObj) { oldObj.Remove("最后更新"); oldObj.Remove("更新者"); }
            if (newComparable is JsonObject newObj) { newObj.Remove("最后更新"); newObj.Remove("更新者"); }
            string oldJson = oldComparable?.ToJsonString() ?? "null";
            string newJson = newComparable?.ToJsonString() ?? "null";
            if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            {
                logger.LogInformation("[记忆] 活文档 {Path} tier {Tier} 没有检测到内容变化，跳过写回和归档", file.Path, tier.Tier);
                return false;
            }

            ArchiveModelDocument(file, fullPath, tier.Tier, doc);
            if (tiersNode is not JsonObject tiersObj) { tiersObj = new JsonObject(); root["分层"] = tiersObj; }
            tiersObj[tier.Tier] = parsed;
            root["生成时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (parsed is JsonObject sec) sec["最后更新"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await AtomicWriteTextAsync(fullPath, json);
            logger.LogInformation("[记忆] 活文档 {Path} tier {Tier} 已刷新", file.Path, tier.Tier);
            return true;
        }
        catch (Exception e) { logger.LogWarning(e, "活文档 {Path} 刷新失败", file?.Path); return false; }
        finally { Interlocked.Exchange(ref generatingExtra, 0); }
    }

    /// <summary>长期层里由反思环节管理的键（如「成长轨迹」）不依赖模型产出：刷新后若缺失，从旧分节原样补回。</summary>
    static void PreserveManagedKeys(JsonNode? current, JsonNode? parsed)
    {
        if (current is not JsonObject curObj || parsed is not JsonObject newObj) return;
        JsonObject curContent = EffectiveLongTermObject(curObj);
        JsonObject newContent = EffectiveLongTermObject(newObj);
        if (curContent.TryGetPropertyValue("成长轨迹", out JsonNode? growth) && growth != null
            && !newContent.ContainsKey("成长轨迹"))
            newContent["成长轨迹"] = growth.DeepClone();
    }

    /// <summary>结构校验：模型产出不得缩水（原有维度/键必须保留）或携带占位符/控制标记。兼容维度数组与扁平键值两种分节形态。</summary>
    static bool ValidateModelSection(JsonNode? current, JsonNode parsed, out string reason)
    {
        reason = "";
        if (current is not JsonObject curObj || parsed is not JsonObject newObj) return true;

        JsonArray? oldDims = curObj["维度"] as JsonArray;
        JsonArray? newDims = newObj["维度"] as JsonArray;
        if (oldDims != null || newDims != null)
        {
            if (oldDims == null || newDims == null) { reason = "维度数组缺失"; return false; }
            var oldNames = DimNames(oldDims);
            var newNames = DimNames(newDims);
            if (oldNames.Count == 0 || !oldNames.IsSubsetOf(newNames))
            {
                reason = $"原有维度被删除或缩水（原{oldNames.Count}，新{newNames.Count}）";
                return false;
            }
            foreach (JsonNode? node in newDims)
            {
                if (node is not JsonObject dim) { reason = "维度数组含非对象项"; return false; }
                if (ContainsPlaceholder(dim["名称"]?.ToString())
                    || ContainsPlaceholder(dim["内容"]?.ToString() ?? dim["备注"]?.ToString() ?? ""))
                { reason = "检测到占位符/模型控制标记"; return false; }
            }
            // 内容清空防线：原有非空维度不得被大规模清空（允许逐步瘦身但至少保留一半）——
            // 防 06:43 事故复发：模型保留全部维度名但把内容写空，此前的名校验拦不住
            var oldFilled = new HashSet<string>();
            foreach (JsonNode? node in oldDims)
                if (node is JsonObject od && !string.IsNullOrWhiteSpace(od["内容"]?.ToString() ?? od["备注"]?.ToString()))
                    oldFilled.Add(od["名称"]?.ToString() ?? "");
            if (oldFilled.Count >= 2)
            {
                int kept = 0;
                foreach (JsonNode? node in newDims)
                    if (node is JsonObject nd && oldFilled.Contains(nd["名称"]?.ToString() ?? "")
                        && !string.IsNullOrWhiteSpace(nd["内容"]?.ToString() ?? nd["备注"]?.ToString()))
                        kept++;
                if (kept * 2 < oldFilled.Count)
                { reason = $"非空维度被大规模清空（原 {oldFilled.Count} 个有内容，新仅 {kept} 个）"; return false; }
            }
            return true;
        }

        // 扁平键值形态（如 SelfModel 长期层）：除元字段与系统管理键外，原有非空键必须保留且非空。
        var metaKeys = new HashSet<string> { "最后更新", "更新者", "名称", "频次", "可容忍过期", "可容忍过时", "成长轨迹" };
        foreach (var kv in curObj)
        {
            if (metaKeys.Contains(kv.Key) || kv.Value is JsonObject or JsonArray) continue;
            if (string.IsNullOrWhiteSpace(kv.Value?.ToString())) continue;
            if (!newObj.ContainsKey(kv.Key) || string.IsNullOrWhiteSpace(newObj[kv.Key]?.ToString()))
            {
                reason = $"原有键被删除或清空：{kv.Key}";
                return false;
            }
        }
        foreach (var kv in newObj)
        {
            if (kv.Key != "成长轨迹" && (ContainsPlaceholder(kv.Key) || ContainsPlaceholder(kv.Value?.ToString() ?? "")))
            { reason = "检测到占位符/模型控制标记"; return false; }
        }
        return true;
    }

    static HashSet<string> DimNames(JsonArray dims) => dims
        .OfType<JsonObject>()
        .Select(d => d["名称"]?.ToString())
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .ToHashSet(StringComparer.Ordinal);

    static bool ContainsPlaceholder(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || s.Contains("no_hallucination", StringComparison.OrdinalIgnoreCase)
            || s.Contains("__key__", StringComparison.Ordinal)
            || s.Contains("__id__", StringComparison.Ordinal);
    }

    static JsonNode? MergeModelUpdates(JsonNode? current, JsonNode parsed)
    {
        if (parsed is not JsonArray updates || current is not JsonObject currentObj)
            return parsed;

        if (currentObj["维度"] is not JsonArray dims)
            return currentObj;

        var clone = JsonNode.Parse(currentObj.ToJsonString())!.AsObject();
        var cloneDims = clone["维度"]!.AsArray();
        foreach (JsonNode? updateNode in updates)
        {
            if (updateNode is not JsonObject update) continue;
            string name = update["名称"]?.ToString() ?? update["标识"]?.ToString() ?? "";
            if (name.Length == 0) continue;
            string content = update["内容"]?.ToString() ?? update["备注"]?.ToString() ?? "";
            foreach (JsonNode? dimNode in cloneDims)
            {
                if (dimNode is not JsonObject dim) continue;
                string dimName = dim["名称"]?.ToString() ?? dim["标识"]?.ToString() ?? "";
                if (!string.Equals(dimName, name, StringComparison.Ordinal)) continue;
                if (dim.ContainsKey("内容")) dim["内容"] = content;
                else dim["备注"] = content;
                break;
            }
        }
        return clone;
    }

    async Task<DateTime> ReadModelTierLastRunAsync(string fullPath, string tier)
    {
        try
        {
            if (!File.Exists(fullPath)) return default;
            JsonObject? root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(fullPath));
            string? lu = root?["分层"]?[tier]?["最后更新"]?.ToString();
            if (DateTime.TryParse(lu, out var dt)) return dt;
        }
        catch { }
        return default;
    }

    /// <summary>把旧版完整文档追加进归档日志（Memory\{名}.archive.jsonl），可回看、可回滚。</summary>
    void ArchiveModelDocument(MemoryModelFileConfig file, string fullPath, string tier, string doc)
    {
        try
        {
            string dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir)) dir = storagePath;
            string archivePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(fullPath) + ".archive.jsonl");
            string line = JsonSerializer.Serialize(new {
                archived_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                tier,
                doc
            });
            Directory.CreateDirectory(storagePath);
            File.AppendAllText(archivePath, line + Environment.NewLine);

            // 轮转：归档超过 4MB 只保留最近 40 个快照，防止无限增长（回滚依赖最近版本即可）。
            FileInfo info = new(archivePath);
            if (info.Length > 4_000_000)
            {
                string[] keep = File.ReadAllLines(archivePath).TakeLast(40).ToArray();
                File.WriteAllLines(archivePath, keep);
                logger.LogInformation("[记忆] 归档 {Archive} 已轮转至最近 {Count} 个快照", Path.GetFileName(archivePath), keep.Length);
            }
        }
        catch (Exception e) { logger.LogWarning(e, "活文档 {Path} 归档失败", file?.Path); }
    }

    /// <summary>原子写（临时文件 + 替换），根治断电损坏（9/8 教训）。</summary>
    static async Task AtomicWriteTextAsync(string fullPath, string text)
    {
        string tmp = fullPath + ".tmp";
        await File.WriteAllTextAsync(tmp, text);
        File.Move(tmp, fullPath, overwrite: true);
    }

    /// <summary>容忍 ```json 围栏、前后缀说明等噪音，解析出 JSON 节点。</summary>
    static JsonNode? TryParseJsonNode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int start = text.IndexOf('\n');
            int end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
                text = text.Substring(start + 1, end - start - 1).Trim();
        }
        try { return JsonNode.Parse(text); }
        catch { }

        // 模型可能在前后加了解释文字：抠出第一个 {...} 或 [...] 对象
        int first = text.IndexOf('{');
        int last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            try { return JsonNode.Parse(text.Substring(first, last - first + 1)); }
            catch { }
        }
        first = text.IndexOf('[');
        last = text.LastIndexOf(']');
        if (first >= 0 && last > first)
        {
            try { return JsonNode.Parse(text.Substring(first, last - first + 1)); }
            catch { }
        }
        return null;
    }

    /// <summary>把活模型文档按「分层名 + 每项名称/内容」渲染成紧凑文本（只取名称与内容，不截断）。</summary>
    async Task<string> RenderModelContextTextAsync(string fullPath, string? onlyTier = null, string? excludeTier = null)
    {
        try
        {
            JsonObject? root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(fullPath));
            if (root == null) return "";
            if (root["分层"] is not JsonObject tiersObj) return "";
            var sb = new StringBuilder();
            foreach (var pair in tiersObj)
            {
                if (onlyTier != null && pair.Key != onlyTier) continue;
                if (excludeTier != null && pair.Key == excludeTier) continue;
                string tierName = pair.Key;
                if (pair.Value is JsonObject t)
                    tierName = t["名称"]?.ToString() ?? pair.Key;
                sb.Append("【").Append(tierName).AppendLine("】");
                if (pair.Value is JsonObject tt && tt["维度"] is JsonArray dims)
                {
                    foreach (var d in dims)
                    {
                        if (d is not JsonObject dim) continue;
                        string name = dim["名称"]?.ToString() ?? dim["标识"]?.ToString() ?? "";
                        string content = dim["内容"]?.ToString() ?? dim["备注"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(content)) continue;
                        sb.Append("- ").Append(name);
                        if (!string.IsNullOrWhiteSpace(content)) sb.Append('：').Append(content);
                        sb.AppendLine();
                    }
                }
                else if (pair.Value is JsonObject flat)
                {
                    // 兼容扁平长期层，也兼容历史遗留的「长期层.长期层」自嵌套；嵌套对象必须展开，不能直接 ToString 成转义 JSON。
                    JsonObject contentObject = flat["长期层"] is JsonObject nested
                        && flat.All(item => item.Key is "长期层" or "最后更新" or "更新者" or "名称" or "频次" or "可容忍过时")
                        ? nested
                        : flat;
                    foreach (var item in contentObject)
                    {
                        if (item.Key is "最后更新" or "更新者" or "名称" or "频次" or "可容忍过时") continue;
                        if (item.Value is JsonObject or JsonArray) continue;
                        string content = item.Value?.ToString() ?? "";
                        if (content.Length == 0) continue;
                        // 多行内容（如成长轨迹）续行缩进两格，保持列表可读
                        sb.Append("- ").Append(item.Key).Append('：').AppendLine(content.Replace("\r\n", "\n").Replace("\n", "\n  "));
                    }
                }
            }
            return sb.ToString().TrimEnd();
        }
        catch { return ""; }
    }

    /// <summary>上下文渲染的称谓归一：历史内容里的「用户」统一成「哥哥」。内容本身保持原样——逐句改写会歪曲事实（如「哥哥希望…」被改成「我希望…」）。</summary>
    static string RenderAsFirstPerson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return text.Replace("用户模型", "哥哥的模型", StringComparison.Ordinal)
                   .Replace("用户", "哥哥", StringComparison.Ordinal);
    }

    /// <summary>所有模型注入块的标记（当前配置推导 + 历史遗留的中英文标记），用于块清理与自引用排除。</summary>
    List<string> AllModelMarkers()
    {
        var markers = new List<string>();
        foreach (var file in Configuration.SemanticModelFiles ?? new List<MemoryModelFileConfig>())
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            markers.Add(string.IsNullOrWhiteSpace(file.Marker)
                ? $"[{Path.GetFileNameWithoutExtension(file.Path)}]"
                : file.Marker);
            string fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (string.Equals(fileName, "SelfModel", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add("[自模型]");
                markers.Add("[我眼中的自己]");
            }
            if (string.Equals(fileName, "SelfModel", StringComparison.OrdinalIgnoreCase))
                markers.Add("[SelfModel]");
            if (string.Equals(fileName, "UserModel", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add("[哥哥模型]");
                markers.Add("[用户模型]");
                markers.Add("[UserModel]");
                markers.Add("[我眼中的哥哥]");
            }
        }
        return markers;
    }

    /// <summary>把活模型文档渲染成 System 块，插到情景存档区（[记忆存档] 块）之前；先移除旧块再重插，保证上下文与磁盘同步。</summary>
    async Task SyncModelContextAfterStartAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), DestroyCancellationToken);
            await BuildModelContextAsync();
            await RefreshTrailingStateAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogWarning(e, "重载后同步阿宵自模型/哥哥模型失败"); }
    }

    async Task BuildModelContextAsync()
    {
        if (!Configuration.SemanticEnable) return;
        if (Configuration.SemanticModelFiles == null) return;

        var blocks = new List<ChatMessageContent>();
        foreach (var file in Configuration.SemanticModelFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) continue;
            // 只提取「分层名 + 每项名称/内容」的紧凑文本，不截断
            // 任务层单独走尾部状态区（[当前状态]）——这里只渲染 长期层+治理层（身份区，天级变化，保缓存稳定）
            string fileName = Path.GetFileNameWithoutExtension(file.Path);
            string text = await RenderModelContextTextAsync(fullPath, excludeTier: "会话任务层");
            if (text.Length == 0) continue;
            if (string.Equals(fileName, "SelfModel", StringComparison.OrdinalIgnoreCase))
            {
                string? anchor = BuildIdentityAnchorText();
                if (!string.IsNullOrWhiteSpace(anchor)) text = anchor + "\n" + text;
            }
            if (string.Equals(fileName, "UserModel", StringComparison.OrdinalIgnoreCase))
                text = "以下是我眼中的哥哥——我对他的理解只代表我此刻的认知：\n" + RenderAsFirstPerson(text);
            // 名称：自模型→我眼中的自己、哥哥模型→我眼中的哥哥
            string marker = string.Equals(fileName, "SelfModel", StringComparison.OrdinalIgnoreCase) ? "[我眼中的自己]"
                : string.Equals(fileName, "UserModel", StringComparison.OrdinalIgnoreCase) ? "[我眼中的哥哥]"
                : (string.IsNullOrWhiteSpace(file.Marker) ? $"[{fileName}]" : file.Marker);
            blocks.Add(new ChatMessageContent(AuthorRole.System, $"{marker}\n{text}"));
        }
        if (blocks.Count == 0) return;

        ChatBot.EditChatHistory(thread => {
            var h = thread.ChatHistory;
            List<string> markers = AllModelMarkers();
            // 先比对：目标块已按原顺序存在、且没有旧标记残留时，整体跳过——无变化的写入会打断 UI 渲染（枚举中改列表会让界面崩溃）
            int bi = 0; bool identical = true;
            for (int i = 0; i < h.Count && bi < blocks.Count; i++)
            {
                var mm = h[i];
                if (mm.Role != AuthorRole.System || mm.Content == null) continue;
                if (markers.Any(mk => mm.Content.StartsWith(mk, StringComparison.Ordinal)))
                {
                    if (mm.Content == blocks[bi].Content) bi++;
                    else { identical = false; break; }
                }
            }
            if (identical && bi == blocks.Count) return;
            // 按新旧 marker 前缀移除旧模型块（含历史遗留的英文/旧中文标记）。
            for (int i = h.Count - 1; i >= 0; i--)
            {
                if (h[i].Role != AuthorRole.System || h[i].Content == null) continue;
                if (markers.Any(mk => h[i].Content.StartsWith(mk, StringComparison.Ordinal)))
                    h.RemoveAt(i);
            }
            // self 在前、user 在后，统一插到情景存档区之前（首个非 System 消息处）
            int idx = 0;
            while (idx < h.Count && h[idx].Role == AuthorRole.System) idx++;
            int at = Math.Min(idx, h.Count);
            for (int i = 0; i < blocks.Count; i++)
                h.Insert(at + i, blocks[i]);
        }, "注入阿宵自模型/哥哥模型");
    }

    // ═══════════ 反思环节 / 一致性治理 / 共同经历召回 ═══════════

    const string SharedExperienceMarker = "[共同经历]";

    string L1FolderPath => Path.Combine(storagePath, "L1");

    string ResolveMemoryPath(string p) => Path.IsPathRooted(p) ? p : Path.Combine(storagePath, p);

    static readonly JsonSerializerOptions ModelJsonOpts = new() {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 主对话让路闸：AI 正在处理用户回合（生成回复/执行工具）时，记忆蒸馏调用先等待其完成再发出，
    /// 避免与主对话在同一供应商的并发额度上互相挤压（2026-11-02 03:19 卡消息事故根因）。
    /// 最多等 YieldMaxWaitSeconds 秒，超时放行并记日志（软让路，不饿死记忆任务）。
    /// </summary>
    async Task YieldToMainChatAsync(string label)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool waited = false;
        while (functionService.IsIdle == false && sw.Elapsed.TotalSeconds < Math.Max(0, Configuration.YieldMaxWaitSeconds))
        {
            waited = true;
            await Task.Delay(500);
        }
        if (waited)
            logger.LogInformation("[记忆] {Label} 让路主对话，等待 {Sec:F0} 秒后放行（IsIdle={Idle}）",
                label, sw.Elapsed.TotalSeconds, functionService.IsIdle);
    }

    /// <summary>蒸馏调用统一入口：让路闸 + 产出跑飞熔断（流式中累计字符超上限即中止；fail-safe 下轮重试）。
    /// capOverride&gt;0 时用任务专属上限（如活文档分节全文重写），否则用全局 DistillMaxOutputChars。</summary>
    async Task<ChatResult> DistillChatAsync(ChatHistoryAgentThread thread, string errorLabel, int capOverride = 0)
    {
        await YieldToMainChatAsync("记忆蒸馏");
        int cap = Math.Max(0, capOverride > 0 ? capOverride : Configuration.DistillMaxOutputChars);
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
        int received = 0;
        void CountChunk(string? s)
        {
            if (s == null || cap <= 0) return;
            received += s.Length;
            if (received > cap) abortCts.Cancel();
        }
        ChatResult result;
        try
        {
            result = await compressModel.ChatStreamingAsync(thread, textReceived: CountChunk, thinkReceived: CountChunk, cancellationToken: abortCts.Token);
        }
        catch (OperationCanceledException) when (received > cap && !DestroyCancellationToken.IsCancellationRequested)
        {
            throw new Exception($"{errorLabel}：产出 {received} 字符超过上限 {cap}，疑似跑飞已中止（下轮 fail-safe 重试）");
        }
        // 取消与流结束赛跑：流正常结束时也可能已超限——此时产出是截断的，必须丢弃（否则会把半截 JSON 当结果）
        if (cap > 0 && received > cap)
            throw new Exception($"{errorLabel}：产出 {received} 字符超过上限 {cap}（流已正常结束），产出不完整已丢弃（下轮 fail-safe 重试）");
        return result;
    }

    async Task<string?> AskCompressModelAsync(string instruction, string errorLabel)
    {
        if (compressModel == null)
            throw new InvalidOperationException("压缩模型尚未就绪（OnStart 仍在初始化，通常在等向量化器加载），请稍后重试");
        ChatHistoryAgentThread miniThread = new();
        miniThread.ChatHistory.AddUserMessage(instruction);
        ChatResult result = await DistillChatAsync(miniThread, errorLabel);
        if (result.Exception != null)
            throw new Exception(errorLabel, result.Exception);
        return result.AIMessage;
    }

    /// <summary>
    /// 以当前对话快照为前缀调用压缩模型：前缀与主对话一致，命中模型供应商的前缀缓存，
    /// 大幅降低「素材就是完整对话」类蒸馏调用的输入成本。素材在历史里，instruction 只写要求。
    /// maxChars&gt;0 时从最旧消息开始丢弃，保留最近部分（上下文预算）。
    /// </summary>
    async Task<string?> AskWithHistoryPrefixAsync(string instruction, string errorLabel, int maxChars = 0, int capOverride = 0)
    {
        if (compressModel == null)
            throw new InvalidOperationException("压缩模型尚未就绪（OnStart 仍在初始化，通常在等向量化器加载），请稍后重试");
        await YieldToMainChatAsync("记忆蒸馏");
        ChatHistoryAgentThread miniThread = new();
        try
        {
            List<ChatMessageContent> snapshot = new();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    snapshot = new List<ChatMessageContent>();
                    foreach (var m in ChatBot.ChatHistory)
                        if (!string.IsNullOrWhiteSpace(m.Content))
                            snapshot.Add(new ChatMessageContent(m.Role, m.Content));
                    break;
                }
                catch (InvalidOperationException) when (attempt < 2)
                { await Task.Delay(150); } // 枚举撞上并发修改：稍候重试
            }
            if (maxChars > 0)
                while (snapshot.Count > 1 && snapshot.Sum(x => x.Content?.Length ?? 0) > maxChars)
                    snapshot.RemoveAt(0);
            foreach (var m in snapshot) miniThread.ChatHistory.Add(m);
        }
        catch (Exception e) { logger.LogWarning(e, "复制对话快照失败，退回独立上下文"); }
        miniThread.ChatHistory.AddUserMessage(instruction);
        // 5.2.1：用量经 ITokenUser 事件发布，本调用期间局部订阅捕获
        TokenUsageContext? usedCtx = null;
        void Capture(TokenUsageContext c) => usedCtx = c;
        Action<TokenUsageContext> capture = Capture;
        ChatResult result;
        try
        {
            tokenUser.TokenUsed += capture;
            result = await DistillChatAsync(miniThread, errorLabel, capOverride);
        }
        finally { tokenUser.TokenUsed -= capture; }
        if (result.Exception != null)
            throw new Exception(errorLabel, result.Exception);
        if (usedCtx.HasValue)
            logger.LogInformation("[记忆] 蒸馏调用 TokenUsage Total={Total} Cached={Cached}", usedCtx.Value.TokenUsage.Total, usedCtx.Value.TokenUsage.Cached);
        return result.AIMessage;
    }

    /// <summary>反思环节：对比「过去的我」与「现在的我」+ 近期经历，产出洞察与长期层更新，原地合并写回（先归档、失败保留原文档）。</summary>
    async Task RunReflectionAsync(ReflectionConfig cfg)
    {
        if (Volatile.Read(ref generatingExtra) == 1)
        {
            logger.LogInformation("[记忆] 另一个记忆生成任务进行中，本次调用跳过（周期触发会兜底）");
            return;
        }
        Interlocked.Exchange(ref generatingExtra, 1);
        try
        {
            foreach (string rel in cfg.TargetFiles ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(rel)) continue;
                string fullPath = ResolveMemoryPath(rel);
                if (!File.Exists(fullPath)) continue;
                string doc = await File.ReadAllTextAsync(fullPath);
                string oldLongTerm = ExtractArchivedLongTerm(fullPath, Math.Max(1, cfg.MaxArchiveLookback));
                string episodic = ReadNewestEpisodicExcerpt(Math.Max(500, cfg.MaxEpisodicChars));

                // 第④份材料：记忆体检薄弱项议程（新鲜才注入；提示词无占位时追加到末尾，兼容旧配置）
                string? examAgenda = BuildExamAgendaText(ReadExamWeakRoot(), DateTime.Now, Math.Max(1, cfg.ExamAgendaMaxAgeDays), Math.Max(500, cfg.ExamAgendaMaxChars));
                string reflectionPrompt = cfg.Prompt ?? "";
                if (examAgenda != null && !reflectionPrompt.Contains("{exam_gaps}"))
                    reflectionPrompt += "\n\n【记忆体检薄弱项（第④份材料·反思议程）】\n" + examAgenda;
                string instruction = reflectionPrompt
                    .Replace("{doc}", Clip(doc, 8000))
                    .Replace("{old_long_term}", oldLongTerm)
                    .Replace("{episodic}", episodic)
                    .Replace("{exam_gaps}", examAgenda ?? "");
                string? raw = await AskCompressModelAsync(instruction, "反思模型调用失败");

                JsonNode? node = TryParseJsonNode(raw ?? "");
                if (node is not JsonObject obj)
                {
                    logger.LogWarning("反思产出非 JSON 对象，保留原文档，原始输出：{Raw}", Clip(raw ?? "", 400));
                    continue;
                }
                var insights = obj["洞察"] as JsonArray;
                var updates = obj["长期层更新"] as JsonArray;
                if ((insights == null || insights.Count == 0) && (updates == null || updates.Count == 0))
                {
                    if (insights == null && updates == null)
                        logger.LogWarning("反思产出缺少 洞察/长期层更新 字段，保留原文档，原始输出：{Raw}", Clip(raw ?? "", 400));
                    else
                        logger.LogInformation("[记忆] 反思：{Path} 未发现变化，跳过写回", rel);
                    continue;
                }

                JsonObject? root;
                try { root = JsonSerializer.Deserialize<JsonObject>(doc); }
                catch { logger.LogWarning("反思目标 {Path} 解析失败，跳过", rel); continue; }
                if (root == null || root["分层"]?["长期层"] is not JsonObject longTerm) continue;
                JsonObject content = EffectiveLongTermObject(longTerm);
                JsonObject beforeContent = (JsonObject)content.DeepClone();

                ApplyLongTermUpdates(content, updates ?? new JsonArray());
                AppendGrowthLog(content, insights ?? new JsonArray(), Math.Max(3, cfg.MaxGrowthEntries));
                int restored = EnforceProtectedLongTermKeys(beforeContent, content);
                if (restored > 0)
                    logger.LogWarning("[记忆][身份锚] 反思改写受保护内容 {N} 处，已还原：{Path}", restored, rel);
                AppendTraitChanges(rel, "长期层(反思)", beforeContent, content);

                ArchiveModelDocument(new MemoryModelFileConfig { Path = rel }, fullPath, "长期层(反思)", doc);
                longTerm["最后更新"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                root["生成时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                await AtomicWriteTextAsync(fullPath, root.ToJsonString(ModelJsonOpts));
                logger.LogInformation("[记忆] 反思：{Path} 长期层已整合（洞察 {I} 条，更新 {U} 条）",
                    rel, insights?.Count ?? 0, updates?.Count ?? 0);
            }
            await BuildModelContextAsync();
        }
        catch (Exception e) { logger.LogWarning(e, "反思环节失败"); }
        finally { Interlocked.Exchange(ref generatingExtra, 0); }
    }

    /// <summary>取长期层实际承载内容的对象：兼容历史遗留的自嵌套（长期层.长期层，内容键在内层）。</summary>
    static JsonObject EffectiveLongTermObject(JsonObject longTerm)
    {
        if (longTerm.TryGetPropertyValue("长期层", out var inner) && inner is JsonObject innerObj
            && longTerm.All(kv => kv.Key is "长期层" or "最后更新" or "更新者" or "名称" or "频次" or "可容忍过期"))
            return innerObj;
        return longTerm;
    }

    /// <summary>把反思产出的长期层更新合并进内容对象：同名覆盖、新键新增（兼容维度数组与扁平键值两种形态）。</summary>
    static void ApplyLongTermUpdates(JsonObject content, JsonArray updates)
    {
        foreach (JsonNode? u in updates)
        {
            if (u is not JsonObject upd) continue;
            string? name = upd["名称"]?.ToString() ?? upd["键"]?.ToString() ?? upd["键名"]?.ToString();
            JsonNode? value = upd["内容"] ?? upd["值"] ?? upd["备注"];
            if (string.IsNullOrWhiteSpace(name) || value == null) continue;

            if (content["维度"] is JsonArray dims)
            {
                bool merged = false;
                foreach (JsonNode? d in dims)
                {
                    if (d is not JsonObject dim) continue;
                    string dn = dim["名称"]?.ToString() ?? dim["标识"]?.ToString() ?? "";
                    if (!string.Equals(dn, name, StringComparison.Ordinal)) continue;
                    if (dim.ContainsKey("内容")) dim["内容"] = value.DeepClone();
                    else dim["备注"] = value.DeepClone();
                    merged = true;
                    break;
                }
                if (!merged)
                {
                    JsonObject nd = new() {
                        ["标识"] = "reflected_" + Guid.NewGuid().ToString("N")[..8],
                        ["名称"] = name,
                        ["内容"] = value.DeepClone(),
                        ["必要性"] = "中",
                        ["更新触发"] = new JsonArray("反思整合"),
                        ["自修改"] = "partial"
                    };
                    if (upd["理由"] != null) nd["理由"] = upd["理由"]!.DeepClone();
                    dims.Add(nd);
                }
                continue;
            }
            content[name] = value.DeepClone();
        }
    }

    /// <summary>把洞察追加进长期层「成长轨迹」（按日期行，只保留最近 maxEntries 条）。</summary>
    static void AppendGrowthLog(JsonObject content, JsonArray insights, int maxEntries)
    {
        if (insights.Count == 0) return;
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        List<string> lines = new();
        if (content.TryGetPropertyValue("成长轨迹", out var old) && old != null)
        {
            foreach (string line in (old.ToString() ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) lines.Add(t);
            }
        }
        foreach (JsonNode? i in insights)
        {
            string? text = i?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) lines.Add($"{today}：{text.Trim()}");
        }
        content["成长轨迹"] = string.Join("\n", lines.TakeLast(maxEntries));
    }

    /// <summary>从归档日志里取最近几版的长期层，作为「过去的我」。</summary>
    string ExtractArchivedLongTerm(string fullPath, int lookback)
    {
        try
        {
            string archivePath = Path.Combine(
                Path.GetDirectoryName(fullPath) ?? storagePath,
                Path.GetFileNameWithoutExtension(fullPath) + ".archive.jsonl");
            if (!File.Exists(archivePath)) return "（暂无归档）";
            string[] lines = File.ReadAllLines(archivePath);
            List<(string at, string section)> picked = new();
            for (int i = lines.Length - 1; i >= 0 && picked.Count < lookback; i--)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    JsonObject? entry = JsonSerializer.Deserialize<JsonObject>(lines[i]);
                    string? tier = entry?["tier"]?.ToString();
                    string? oldDoc = entry?["doc"]?.ToString();
                    if (string.IsNullOrWhiteSpace(oldDoc)) continue;
                    if (tier != "长期层" && tier != "长期层(反思)") continue;
                    JsonObject? oldRoot = JsonSerializer.Deserialize<JsonObject>(oldDoc);
                    if (oldRoot?["分层"]?["长期层"] is not JsonNode section) continue;
                    picked.Add((entry?["archived_at"]?.ToString() ?? "?", section.ToJsonString(ModelJsonOpts)));
                }
                catch { }
            }
            if (picked.Count == 0) return "（暂无长期层归档）";
            StringBuilder sb = new();
            foreach (var (at, section) in picked)
            {
                sb.Append("──归档于 ").Append(at).AppendLine("──");
                sb.AppendLine(Clip(section, 2000));
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "读取归档长期层失败");
            return "（归档读取失败）";
        }
    }

    /// <summary>取最新的几份 L1 情景存档作为近期经历摘录（每份截断，总长受限）。</summary>
    string ReadNewestEpisodicExcerpt(int maxChars)
    {
        try
        {
            if (!Directory.Exists(L1FolderPath)) return "（暂无情景存档）";
            List<string> files = Directory.GetFiles(L1FolderPath, "*.txt")
                .OrderByDescending(f => Path.GetFileName(f))
                .Take(2)
                .ToList();
            if (files.Count == 0) return "（暂无情景存档）";
            int per = Math.Max(300, maxChars / files.Count);
            StringBuilder sb = new();
            foreach (string f in files)
            {
                sb.Append("──情景存档 ").Append(Path.GetFileName(f)).AppendLine("──");
                sb.AppendLine(Clip(File.ReadAllText(f), per));
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "读取情景存档摘录失败");
            return "（情景存档读取失败）";
        }
    }

    /// <summary>一致性治理：角色卡 ↔ 自模型矛盾审查，报告写进治理层维度「一致性报告（人设卡对照）」（只报告，不改角色卡/核心维度）。</summary>
    async Task RunConsistencyAsync(ConsistencyConfig cfg)
    {
        if (Volatile.Read(ref generatingExtra) == 1)
        {
            logger.LogInformation("[记忆] 另一个记忆生成任务进行中，本次调用跳过（周期触发会兜底）");
            return;
        }
        Interlocked.Exchange(ref generatingExtra, 1);
        try
        {
            string targetPath = ResolveMemoryPath(string.IsNullOrWhiteSpace(cfg.TargetPath) ? "model/SelfModel.json" : cfg.TargetPath);
            if (!File.Exists(targetPath)) return;
            string personaPath = string.IsNullOrWhiteSpace(cfg.PersonaPath)
                ? Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "index.json")
                : ResolveMemoryPath(cfg.PersonaPath);
            string persona = ExtractPersonaText(personaPath, Math.Max(500, cfg.MaxPersonaChars));
            if (persona.Length == 0)
            {
                logger.LogWarning("一致性治理：角色卡不可读（{Path}）", personaPath);
                return;
            }
            string doc = await File.ReadAllTextAsync(targetPath);

            string instruction = (cfg.Prompt ?? "")
                .Replace("{persona}", persona)
                .Replace("{doc}", Clip(doc, 8000));
            string? raw = await AskCompressModelAsync(instruction, "一致性审查模型调用失败");
            JsonNode? node = TryParseJsonNode(raw ?? "");
            if (node is not JsonObject obj || (obj["矛盾"] == null && obj["结论"] == null))
            {
                logger.LogWarning("一致性审查产出非 JSON，跳过本次，原始输出：{Raw}", Clip(raw ?? "", 400));
                return;
            }
            string report = BuildConsistencyReport(obj);

            JsonObject? root;
            try { root = JsonSerializer.Deserialize<JsonObject>(doc); }
            catch { logger.LogWarning("一致性目标 {Path} 解析失败", cfg.TargetPath); return; }
            if (root == null || root["分层"]?["治理层"] is not JsonObject govern) return;
            if (govern["维度"] is not JsonArray dims)
            {
                dims = new JsonArray();
                govern["维度"] = dims;
            }

            // 报告无实质变化（忽略首行日期）则不写回、不归档
            string oldReport = "";
            foreach (JsonNode? d in dims)
            {
                if (d is JsonObject dim && string.Equals(dim["名称"]?.ToString(), "一致性报告（人设卡对照）", StringComparison.Ordinal))
                {
                    oldReport = dim["备注"]?.ToString() ?? dim["内容"]?.ToString() ?? "";
                    break;
                }
            }
            if (string.Equals(SkipFirstLine(oldReport), SkipFirstLine(report), StringComparison.Ordinal))
            {
                logger.LogInformation("[记忆] 一致性报告无实质变化，跳过写回");
                return;
            }

            bool found = false;
            foreach (JsonNode? d in dims)
            {
                if (d is JsonObject dim && string.Equals(dim["名称"]?.ToString(), "一致性报告（人设卡对照）", StringComparison.Ordinal))
                {
                    if (dim.ContainsKey("内容")) dim["内容"] = report;
                    else dim["备注"] = report;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                dims.Add(new JsonObject {
                    ["标识"] = "consistency_report",
                    ["名称"] = "一致性报告（人设卡对照）",
                    ["必要性"] = "高",
                    ["后果"] = "人格漂移、设定冲突无人察觉",
                    ["更新触发"] = new JsonArray { "治理周期" },
                    ["自修改"] = "none",
                    ["备注"] = report
                });
            }

            ArchiveModelDocument(new MemoryModelFileConfig { Path = cfg.TargetPath }, targetPath, "治理层(一致性)", doc);
            govern["最后更新"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            root["生成时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await AtomicWriteTextAsync(targetPath, root.ToJsonString(ModelJsonOpts));
            logger.LogInformation("[记忆] 一致性审查完成：{Report}", Clip(report, 160));
            await BuildModelContextAsync();
        }
        catch (Exception e) { logger.LogWarning(e, "一致性治理失败"); }
        finally { Interlocked.Exchange(ref generatingExtra, 0); }
    }

    /// <summary>从角色 index.json 提取人设文本（Name/Description/Prompt 等字段），失败返回空。</summary>
    static string ExtractPersonaText(string personaPath, int maxChars)
    {
        try
        {
            if (!File.Exists(personaPath)) return "";
            JsonObject? root = JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(personaPath));
            if (root == null) return "";
            StringBuilder sb = new();
            foreach (string key in new[] { "Name", "Description", "Prompt", "Requirement", "Requirements" })
            {
                string? v = root[key]?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    sb.Append("【").Append(key).AppendLine("】");
                    sb.AppendLine(v.Trim());
                }
            }
            if (sb.Length == 0) sb.AppendLine(File.ReadAllText(personaPath));
            return Clip(sb.ToString(), maxChars);
        }
        catch { return ""; }
    }

    static string BuildConsistencyReport(JsonObject obj)
    {
        StringBuilder sb = new();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).AppendLine(" 审查");
        var conflicts = obj["矛盾"] as JsonArray;
        string? verdict = obj["结论"]?.ToString();
        int n = conflicts?.Count(c => c is JsonObject) ?? 0;
        sb.Append("结论：").AppendLine(string.IsNullOrWhiteSpace(verdict)
            ? (n == 0 ? "未发现实质矛盾" : $"发现 {n} 处需注意")
            : verdict!.Trim());
        int i = 0;
        if (conflicts != null)
        {
            foreach (JsonNode? c in conflicts)
            {
                if (c is not JsonObject conf) continue;
                i++;
                sb.Append(i).Append(". [").Append(conf["评定"]?.ToString() ?? "未评定").Append("] ")
                    .AppendLine(conf["主题"]?.ToString() ?? "");
                void Field(string key)
                {
                    string? v = conf[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) sb.Append("   ").Append(key).AppendLine("：" + v.Trim());
                }
                Field("人设卡说");
                Field("自模型说");
                Field("建议");
            }
        }
        return sb.ToString().TrimEnd();
    }

    static string SkipFirstLine(string s)
    {
        int i = (s ?? "").IndexOf('\n');
        return i < 0 ? "" : s![(i + 1)..].Trim();
    }

    /// <summary>共同经历索引构建：把未索引的 L1 情景存档逐份提炼成结构化条目（人物/情感/主题/摘要），去重后原子写回。</summary>
    async Task RunExperienceIndexAsync(ExperienceRecallConfig cfg)
    {
        if (Volatile.Read(ref generatingExtra) == 1)
        {
            logger.LogInformation("[记忆] 另一个记忆生成任务进行中，本次调用跳过（周期触发会兜底）");
            return;
        }
        Interlocked.Exchange(ref generatingExtra, 1);
        try
        {
            string indexPath = ResolveMemoryPath(string.IsNullOrWhiteSpace(cfg.IndexPath) ? "model/ExperienceIndex.json" : cfg.IndexPath);
            JsonObject root;
            if (File.Exists(indexPath))
            {
                try { root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(indexPath)) ?? new JsonObject(); }
                catch { root = new JsonObject(); }
            }
            else root = new JsonObject();
            JsonArray entries = root["条目"] as JsonArray ?? new JsonArray();
            if (root["条目"] == null) root["条目"] = entries;
            JsonArray processed = root["已索引"] as JsonArray ?? new JsonArray();
            if (root["已索引"] == null) root["已索引"] = processed;

            if (!Directory.Exists(L1FolderPath))
            {
                logger.LogWarning("[记忆] 共同经历索引：L1 目录不存在 {Dir}，跳过", L1FolderPath);
                return;
            }
            var indexedSources = new HashSet<string>();
            foreach (JsonNode? e in entries)
            {
                string? src = (e as JsonObject)?["来源"]?.ToString();
                if (!string.IsNullOrWhiteSpace(src)) indexedSources.Add(src);
            }
            var knownProcessed = new HashSet<string>(processed.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0));

            List<string> pending = Directory.GetFiles(L1FolderPath, "*.txt")
                .Select(f => "L1/" + Path.GetFileName(f))
                .Where(name => !indexedSources.Contains(name) && !knownProcessed.Contains(name))
                .OrderByDescending(name => name)
                .Take(Math.Max(1, cfg.FilesPerRun))
                .ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("[记忆] 共同经历索引：没有未索引的情景存档，跳过");
                return;
            }

            // 迁移：旧条目补 id/状态；事件组容器缺失则创建；计算 ID 起点
            JsonObject groups = root["事件组"] as JsonObject ?? new JsonObject();
            if (root["事件组"] == null) root["事件组"] = groups;
            int nextId = 0;
            foreach (JsonObject eo in entries.OfType<JsonObject>().ToList())
            {
                string? existing = eo["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(existing) && existing.StartsWith("exp-", StringComparison.Ordinal)
                    && int.TryParse(existing.AsSpan(4), out int v) && v > nextId)
                    nextId = v;
            }
            foreach (JsonObject eo in entries.OfType<JsonObject>().ToList())
            {
                if (string.IsNullOrWhiteSpace(eo["id"]?.ToString()))
                {
                    nextId++;
                    eo["id"] = $"exp-{nextId:0000}";
                }
                if (eo["状态"] == null) eo["状态"] = "当前";
            }

            int added = 0, updated = 0, deduped = 0;
            var newItems = new List<JsonObject>();
            var itemSources = new List<string>();
            foreach (string rel in pending)
            {
                processed.Add(rel);
                string excerpt = $"──情景存档 {rel}──\n" + Clip(File.ReadAllText(Path.Combine(storagePath, rel)), Math.Max(500, cfg.MaxSourceChars));
                string instruction = (cfg.IndexPrompt ?? "").Replace("{episodic}", excerpt);
                string? raw = await AskCompressModelAsync(instruction, "共同经历索引模型调用失败");
                JsonNode? node = TryParseJsonNode(raw ?? "");
                if (node is not JsonObject obj || obj["条目"] is not JsonArray arr)
                {
                    logger.LogWarning("共同经历索引：{File} 产出非 JSON，跳过，原始输出：{Raw}", rel, Clip(raw ?? "", 300));
                    continue;
                }
                string fallbackDate = DateFromArchiveName(rel);
                foreach (JsonNode? p in arr)
                {
                    if (p is not JsonObject entry) continue;
                    string? summary = entry["摘要"]?.ToString();
                    if (string.IsNullOrWhiteSpace(summary)) continue;
                    bool dup = false;
                    foreach (JsonNode? ex in entries)
                    {
                        if (ex is not JsonObject existing) continue;
                        if (CharBigramJaccard(summary, existing["摘要"]?.ToString() ?? "") >= 0.55) { dup = true; break; }
                    }
                    if (dup) { deduped++; continue; }

                    JsonObject item = new() {
                        ["时间"] = string.IsNullOrWhiteSpace(entry["时间"]?.ToString()) ? fallbackDate : entry["时间"]!.DeepClone(),
                        ["来源"] = rel,
                        ["人物"] = entry["人物"]?.DeepClone() ?? new JsonArray { "哥哥" },
                        ["情感"] = entry["情感"] != null ? entry["情感"]!.DeepClone() : JsonValue.Create(3),
                        ["主题"] = entry["主题"]?.DeepClone() ?? new JsonArray(),
                        ["摘要"] = summary
                    };
                    newItems.Add(item); itemSources.Add(rel);
                }
            }

            // ── 写入时对账：本地预筛候选 → LLM 判定（新增/更新/重复）→ supersedes 版本链 + 事件组 ──
            if (newItems.Count > 0)
            {
                var verdicts = new List<JsonObject>();
                bool reconciled = false;
                if (cfg.ReconcileEnable)
                {
                    var candTexts = new List<(string id, string text)>();
                    var candSeen = new HashSet<string>();
                    foreach (var ni in newItems)
                    {
                        string nt = (ni["摘要"]?.ToString() ?? "") + (ni["主题"] is JsonArray ntj ? string.Join("", ntj.Select(x => x?.ToString() ?? "")) : "");
                        foreach (var eo in entries.OfType<JsonObject>())
                        {
                            if ((eo["状态"]?.ToString() ?? "当前") != "当前") continue;
                            string id = eo["id"]?.ToString() ?? "";
                            if (id.Length == 0 || candSeen.Contains(id)) continue;
                            string et = (eo["摘要"]?.ToString() ?? "") + (eo["主题"] is JsonArray etj ? string.Join("", etj.Select(x => x?.ToString() ?? "")) : "");
                            if (CharBigramJaccard(nt, et) >= 0.12)
                            {
                                candSeen.Add(id);
                                candTexts.Add((id, et));
                            }
                        }
                    }
                    if (candTexts.Count > 0)
                    {
                        var candArr = new JsonArray();
                        foreach (var (id, text) in candTexts.Take(12))
                            candArr.Add(new JsonObject { ["ID"] = id, ["内容"] = text });
                        var itemsArr = new JsonArray();
                        for (int i = 0; i < newItems.Count; i++)
                            itemsArr.Add(new JsonObject {
                                ["序号"] = i,
                                ["时间"] = newItems[i]["时间"]?.DeepClone(),
                                ["人物"] = newItems[i]["人物"]?.DeepClone(),
                                ["主题"] = newItems[i]["主题"]?.DeepClone(),
                                ["摘要"] = newItems[i]["摘要"]?.DeepClone()
                            });
                        string instruction = (cfg.ReconcilePrompt ?? "")
                            .Replace("{candidates}", candArr.ToJsonString(ModelJsonOpts))
                            .Replace("{items}", itemsArr.ToJsonString(ModelJsonOpts));
                        string? raw = await AskCompressModelAsync(instruction, "共同经历对账模型调用失败");
                        if (TryParseJsonNode(raw ?? "") is JsonObject robj && robj["判定"] is JsonArray rarr)
                        {
                            foreach (JsonNode? v in rarr)
                                if (v is JsonObject vo) verdicts.Add(vo);
                            reconciled = true;
                        }
                        else logger.LogWarning("共同经历对账：产出非 JSON，全部按新增处理，原始输出：{Raw}", Clip(raw ?? "", 300));
                    }
                }
                for (int vi = 0; vi < newItems.Count; vi++) newItems[vi]["来源"] = itemSources[vi];
                (added, updated, deduped, nextId) = ApplyReconciliation(entries, groups, newItems, verdicts, nextId);
                if (reconciled) logger.LogInformation("[记忆] 共同经历对账完成：新增 {A} 更新 {U} 重复 {D}", added, updated, deduped);
            }

            while (entries.Count > Math.Max(20, cfg.MaxIndexEntries))
            {
                int oldest = -1;
                string? oldestKey = null;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is not JsonObject eo) continue;
                    bool covered = (eo["状态"]?.ToString() ?? "当前") == "已覆盖";
                    string key = (covered ? "0" : "1") + (eo["时间"]?.ToString() ?? "");
                    if (oldestKey == null || string.CompareOrdinal(key, oldestKey) < 0) { oldestKey = key; oldest = i; }
                }
                if (oldest < 0) break;
                entries.RemoveAt(oldest);
            }

            root["最后更新"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath) ?? storagePath);
            await AtomicWriteTextAsync(indexPath, root.ToJsonString(ModelJsonOpts));
            logger.LogInformation("[记忆] 共同经历索引：本次新增 {Added} 更新 {Updated} 去重 {Deduped}（共 {Total} 条）", added, updated, deduped, entries.Count);
            await RefreshTrailingStateAsync();
        }
        catch (Exception e) { logger.LogWarning(e, "共同经历索引构建失败"); }
        finally { Interlocked.Exchange(ref generatingExtra, 0); }
    }

    /// <summary>
    /// 把对账判定应用到索引（就地修改 entries/groups）。判定无效、取代目标缺失一律按"新增"兜底，绝不丢内容。
    /// 新条目须带 来源；返回 (新增数, 更新数, 去重数, 下一可用ID)。
    /// </summary>
    static (int added, int updated, int deduped, int nextId) ApplyReconciliation(
        JsonArray entries, JsonObject groups, List<JsonObject> newItems, List<JsonObject> verdicts, int nextId)
    {
        int added = 0, updated = 0, deduped = 0;
        for (int vi = 0; vi < newItems.Count; vi++)
        {
            JsonObject item = newItems[vi];
            JsonObject? verdict = verdicts.FirstOrDefault(v => (v["序号"]?.GetValue<int>() ?? -1) == vi);
            string concl = verdict?["结论"]?.ToString() ?? "新增";
            string groupWish = verdict?["事件组"]?.ToString() ?? "无";

            if (concl == "重复") { deduped++; continue; }

            nextId++;
            string newId = $"exp-{nextId:0000}";
            item["id"] = newId;
            item["状态"] = "当前";

            if (concl == "更新")
            {
                string target = verdict?["取代"]?.ToString() ?? "";
                JsonObject? oldEntry = entries.OfType<JsonObject>()
                    .FirstOrDefault(e => e["id"]?.ToString() == target && (e["状态"]?.ToString() ?? "当前") == "当前");
                if (oldEntry != null)
                {
                    oldEntry["状态"] = "已覆盖";
                    oldEntry["被取代"] = newId;
                    item["取代"] = target;
                    string? oldGroup = oldEntry["事件组"]?.ToString();
                    if (!string.IsNullOrEmpty(oldGroup) && groups[oldGroup] is JsonObject og && og["条目"] is JsonArray ol)
                    {
                        item["事件组"] = oldGroup;
                        ol.Add(newId);
                    }
                    else
                    {
                        string gid = NewEventGroup(groups, item, newId);
                        item["事件组"] = gid;
                        // 旧版本也入链（旧在前），脉络不断历史
                        if (groups[gid]["条目"] is JsonArray gl) gl.Insert(0, target);
                    }
                    entries.Add(item); updated++;
                    continue;
                }
                // 取代目标不存在：兜底走新增
            }

            if (groupWish.StartsWith("沿:", StringComparison.Ordinal))
            {
                string refId = groupWish[2..].Trim();
                JsonObject? refEntry = entries.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.ToString() == refId)
                    ?? entries.OfType<JsonObject>().FirstOrDefault(e => e["事件组"]?.ToString() == refId);
                string? refGroup = refEntry?["事件组"]?.ToString() ?? (groups.ContainsKey(refId) ? refId : null);
                if (!string.IsNullOrEmpty(refGroup) && groups[refGroup] is JsonObject rg && rg["条目"] is JsonArray rl)
                {
                    item["事件组"] = refGroup;
                    rl.Add(newId);
                }
                else
                {
                    string gid = NewEventGroup(groups, item, newId);
                    item["事件组"] = gid;
                    if (refEntry != null)
                    {
                        refEntry["事件组"] = gid;
                        (groups[gid]["条目"] as JsonArray)!.Insert(0, refEntry["id"]!.ToString());
                    }
                }
            }
            else if (groupWish.StartsWith("新组:", StringComparison.Ordinal))
            {
                item["事件组"] = NewEventGroup(groups, item, newId, groupWish[3..].Trim());
            }
            entries.Add(item); added++;
        }
        return (added, updated, deduped, nextId);
    }

    /// <summary>新建事件组（evt-编号），主题取参数或条目第一个主题；条目入组。</summary>
    static string NewEventGroup(JsonObject groups, JsonObject item, string entryId, string? topic = null)
    {
        int max = 0;
        foreach (var kv in groups)
            if (kv.Key.StartsWith("evt-", StringComparison.Ordinal) && int.TryParse(kv.Key.AsSpan(4), out int v) && v > max)
                max = v;
        string gid = $"evt-{max + 1:0000}";
        string t = topic;
        if (string.IsNullOrWhiteSpace(t) && item["主题"] is JsonArray ta && ta.Count > 0) t = ta[0]?.ToString();
        groups[gid] = new JsonObject { ["主题"] = string.IsNullOrWhiteSpace(t) ? "共同事件" : t, ["条目"] = new JsonArray(entryId) };
        return gid;
    }

    static string DateFromArchiveName(string rel)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(rel); // 形如 1-20261002214915-20261003181302
            string[] parts = name.Split('-');
            if (parts.Length >= 2 && parts[1].Length >= 8
                && DateTime.TryParseExact(parts[1][..8], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d))
                return d.ToString("yyyy-MM-dd");
        }
        catch { }
        return DateTime.Now.ToString("yyyy-MM-dd");
    }

    /// <summary>按最近对话对共同经历索引打分，重建 [共同经历] 注入块（纯本地关联匹配，不调 LLM；无关联则只清理旧块）。</summary>
    /// <summary>计算共同经历块文本（话题相关时才有内容；事件组成员渲染为脉络链）。无内容返回 null。</summary>
    async Task<string?> ComputeSharedExperienceTextAsync()
    {
        try
        {
            var cfg = Configuration.ExperienceRecall;
            if (cfg == null || !cfg.Enable) return null;
            string indexPath = ResolveMemoryPath(string.IsNullOrWhiteSpace(cfg.IndexPath) ? "model/ExperienceIndex.json" : cfg.IndexPath);
            if (!File.Exists(indexPath)) return null;
            JsonObject? root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(indexPath));
            if (root?["条目"] is not JsonArray entries || entries.Count == 0) return null;

            string recent = CollectRecentContext(6);
            if (recent.Length == 0) return null;

            // 心绪唤醒偏置：当前越上头（A 高），情感重的事越容易浮现（效价不参与，避免低落反刍螺旋）
            double aNow = 0;
            if (cfg.MoodBiasWeight > 0 && Configuration.Heartbeat is { Enable: true } hbc)
            {
                lock (heartbeatLock)
                {
                    var hbRoot = LoadHeartbeat(HeartbeatPath(), hbc);
                    if (hbRoot["mood"] is JsonObject md && md["A"] != null && double.TryParse(md["A"]!.ToString(), out var a)) aNow = a;
                }
            }
            DateTime now = DateTime.Now;
            var ranked = new List<(double score, JsonObject entry)>();
            foreach (JsonNode? e in entries)
            {
                if (e is not JsonObject entry) continue;
                if ((entry["状态"]?.ToString() ?? "当前") != "当前") continue; // 已被后续取代的旧版本不参与召回
                string summary = entry["摘要"]?.ToString() ?? "";
                if (summary.Length == 0) continue;
                string topics = entry["主题"] is JsonArray t ? string.Join("", t.Select(x => x?.ToString() ?? "")) : "";
                string people = entry["人物"] is JsonArray p ? string.Join("", p.Select(x => x?.ToString() ?? "")) : "";
                double topic = CharBigramJaccard(recent, summary + topics + people);
                if (topic < 0.04) continue; // 话题门：情感/新近度再高也不能把无关经历单独抬进来
                int emotion = entry["情感"] is JsonNode em && int.TryParse(em.ToString(), out int ev) ? Math.Clamp(ev, 1, 5) : 3;
                DateTime tDate = DateTime.TryParse(entry["时间"]?.ToString(), out var td) ? td : now;
                double recency = Math.Max(0, 1 - Math.Max(0, (now - tDate).TotalDays) / 90.0);
                double score = topic * 2.5 + emotion * 0.06 + recency * 0.15 + MoodRecallBias(aNow, emotion, cfg.MoodBiasWeight);
                if (desireEngine != null && Configuration.Desire?.MemoryBiasEnable != false)
                    score += desireEngine.RecallBias(entry);
                if (score < cfg.MinScore) continue;
                ranked.Add((score, entry));
            }
            ranked.Sort((a, b) => b.score.CompareTo(a.score));
            var topEntries = ranked.Take(Math.Max(1, cfg.TopN)).Select(r => r.entry).ToList();

            // 渲染：事件组成员渲染为脉络链（时间线；以条目上的 事件组 字段反查为单一事实源），独立条目渲染单行
            var lines = new List<string>();
            var usedGroups = new HashSet<string>();
            foreach (var e in topEntries)
            {
                string gid = e["事件组"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(gid) && root["事件组"]?[gid] is JsonObject g)
                {
                    if (!usedGroups.Add(gid)) continue; // 同组已渲染
                    var chain = new List<string>();
                    foreach (JsonObject ce in entries.OfType<JsonObject>()
                        .Where(x => (x["事件组"]?.ToString() ?? "") == gid)
                        .OrderBy(x => x["时间"]?.ToString() ?? ""))
                    {
                        chain.Add($"{(ce["时间"]?.ToString() ?? "")} {ce["摘要"]?.ToString()}");
                    }
                    if (chain.Count == 0) continue;
                    string topic = g["主题"]?.ToString() ?? "共同事件";
                    lines.Add($"- 事件脉络「{topic}」：{string.Join(" → ", chain)}");
                }
                else
                {
                    string summary = e["摘要"]?.ToString() ?? "";
                    int emotion = e["情感"] is JsonNode em && int.TryParse(em.ToString(), out int ev) ? Math.Clamp(ev, 1, 5) : 3;
                    string time = e["时间"]?.ToString() ?? "";
                    string who = e["人物"] is JsonArray pp ? string.Join("、", pp.Select(x => x?.ToString() ?? "")) : "哥哥";
                    lines.Add($"- {time}（{who}，情感{emotion}/5）：{summary}");
                }
            }
            if (lines.Count == 0) return null;
            return ToFirstPersonMemory(SharedExperienceMarker + "\n这是我和哥哥共同经历中与当前话题相关的部分，供我自然参考（不要刻意复述）：\n"
                + string.Join("\n", lines));
        }
        catch (Exception e) { logger.LogWarning(e, "共同经历召回计算失败"); return null; }
    }

    /// <summary>计算任务层状态块文本：两份活文档的 会话任务层 合成一个 [当前状态] 块。无内容返回 null。</summary>
    async Task<string?> ComputeTaskStateTextAsync()
    {
        var sb = new StringBuilder();
        foreach (var file in Configuration.SemanticModelFiles ?? new List<MemoryModelFileConfig>())
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            string fullPath = ResolveModelPath(file);
            if (!File.Exists(fullPath)) continue;
            string name = Path.GetFileNameWithoutExtension(file.Path);
            bool isSelf = string.Equals(name, "SelfModel", StringComparison.OrdinalIgnoreCase);
            string text = await RenderTaskLayerCompactAsync(fullPath, isSelf);
            if (text.Length == 0) continue;
            sb.Append(isSelf ? "我：\n" : "哥哥：\n");
            sb.AppendLine(text);
        }
        if (sb.Length == 0) return null;
        string body = sb.ToString().TrimEnd();
        int cap = Math.Max(400, Configuration.TaskStateMaxTotalChars);
        if (body.Length > cap) body = body[..cap] + "…";
        return TaskStateMarker + "\n" + body;
    }

    static readonly string[] SelfTaskPriority =
    {
        "capability_skills", "my_open_threads", "relationship_network", "system_tool_status",
        "discipline_plan", "knowledge_belief", "recent_calibration", "memory_system_state"
    };
    static readonly string[] UserTaskPriority =
    {
        "knowledge_belief", "open_threads", "relationship_network", "self_discipline_plan"
    };

    /// <summary>任务层紧凑渲染：每份文档最多取优先级最高的 N 维，每维截断，去掉标题/元字段。</summary>
    async Task<string> RenderTaskLayerCompactAsync(string fullPath, bool isSelf)
    {
        try
        {
            JsonObject? root = JsonSerializer.Deserialize<JsonObject>(await File.ReadAllTextAsync(fullPath));
            if (root?["分层"]?["会话任务层"] is not JsonObject task) return "";
            var items = new List<(int Rank, string Name, string Content)>();
            if (task["维度"] is JsonArray dims)
            {
                foreach (JsonNode? node in dims)
                {
                    if (node is not JsonObject dim) continue;
                    string id = dim["标识"]?.ToString() ?? "";
                    string name = dim["名称"]?.ToString() ?? id;
                    string content = dim["内容"]?.ToString() ?? dim["备注"]?.ToString() ?? "";
                    if (content.Length == 0) continue;
                    items.Add((TaskPriority(isSelf, id, name), name, content));
                }
            }
            else
            {
                foreach (var kv in task)
                {
                    if (kv.Key is "最后更新" or "更新者" or "名称" or "频次" or "可容忍过期" or "维度") continue;
                    if (kv.Value is JsonObject or JsonArray) continue;
                    string content = kv.Value?.ToString() ?? "";
                    if (content.Length == 0) continue;
                    items.Add((TaskPriority(isSelf, kv.Key, kv.Key), kv.Key, content));
                }
            }
            int maxDims = Configuration.TaskStateMaxDimsPerDoc <= 0 ? items.Count : Configuration.TaskStateMaxDimsPerDoc;
            int maxChars = Math.Max(60, Configuration.TaskStateMaxCharsPerDim);
            var sb = new StringBuilder();
            foreach (var item in items.OrderBy(x => x.Rank).Take(maxDims))
            {
                string content = item.Content.Replace("\r", " ").Replace("\n", " ").Trim();
                int cap = maxChars;
                if (item.Name.Contains("话头", StringComparison.Ordinal) || item.Name.Contains("未获回应", StringComparison.Ordinal))
                    cap = Math.Max(maxChars, 420);
                else if (item.Name.Contains("能力", StringComparison.Ordinal) || item.Name.Contains("工具", StringComparison.Ordinal))
                    cap = Math.Max(maxChars, 320);
                if (content.Length > cap) content = content[..cap] + "…";
                sb.Append("- ").Append(item.Name).Append('：').AppendLine(content);
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception e) { logger.LogWarning(e, "任务层紧凑渲染失败：{Path}", fullPath); return ""; }
    }

    static int TaskPriority(bool isSelf, string id, string name)
    {
        string[] list = isSelf ? SelfTaskPriority : UserTaskPriority;
        for (int i = 0; i < list.Length; i++)
            if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return 100;
    }

    /// <summary>
    /// 尾部状态区：把 [共同经历]（话题相关时）、[当前状态]（两份任务层）、[心绪] 合成一条系统消息挂在上下文末尾。
    /// 三个块都随对话/话题变化，放尾部只重算尾部小段，不打断前部身份区的前缀缓存；内容一致时整体跳过（不碰列表）。
    /// </summary>
    async Task RefreshTrailingStateAsync()
    {
        try
        {
            var parts = new List<string>();
            string? invLine = BuildInventoryLine();
            if (!string.IsNullOrEmpty(invLine)) parts.Add(invLine);
            parts.Add(BuildConfidenceLine());
            string? anchor = BuildAnchorPoolLine();
            if (!string.IsNullOrEmpty(anchor)) parts.Add(anchor);
            string? se = await ComputeSharedExperienceTextAsync();
            if (!string.IsNullOrEmpty(se)) parts.Add(se);
            string? task = await ComputeTaskStateTextAsync();
            if (!string.IsNullOrEmpty(task)) parts.Add(task);
            string? desireText = desireEngine?.BuildInjectionText();
            if (!string.IsNullOrEmpty(desireText)) parts.Add(desireText);
            var hb = Configuration.Heartbeat;
            if (hb is { Enable: true })
            {
                lock (heartbeatLock)
                {
                    JsonObject root = LoadHeartbeat(HeartbeatPath(), hb);
                    parts.Add(BuildHeartbeatBlockText(root));
                }
            }
            string blockText = parts.Count == 0 ? "" : TrailingMarker + "\n" + string.Join("\n\n", parts);
            ChatBot.EditChatHistory(thread => {
                var h = thread.ChatHistory;
                if (parts.Count == 0)
                {
                    RemoveTrailingBlocks(h); // 无内容也要清掉旧块
                    return;
                }
                // 同内容尾块保持原位：每次 remove+append 会把尾块从对话中段挪到末尾，请求前缀随之改变，
                // 供应商的前缀缓存会整段失效（实测小上下文每轮 cached=0）。
                int sameIndex = -1;
                for (int i = 0; i < h.Count; i++)
                {
                    if (h[i].Role != AuthorRole.System || h[i].Content == null) continue;
                    if (!h[i].Content!.StartsWith(TrailingMarker, StringComparison.Ordinal)) continue;
                    if (h[i].Content == blockText) sameIndex = i;
                }
                if (sameIndex >= 0)
                {
                    // 顺手清掉历史遗留的重复尾块，只保留同内容那一块
                    for (int i = h.Count - 1; i >= 0; i--)
                        if (i != sameIndex && h[i].Role == AuthorRole.System && h[i].Content != null
                            && h[i].Content!.StartsWith(TrailingMarker, StringComparison.Ordinal))
                            h.RemoveAt(i);
                    return;
                }
                RemoveTrailingBlocks(h);
                h.Add(new ChatMessageContent(AuthorRole.System, blockText));
            }, "注入尾部状态区");
        }
        catch (Exception e) { logger.LogWarning(e, "尾部状态区刷新失败"); }
    }

    static void RemoveTrailingBlocks(ChatHistory h)
    {
        // 统一头标记 + 各分节标记 + 历史格式（【记忆库存】开头的受损版）——全认，确保旧块一定能被清掉
        var markers = new List<string> { TrailingMarker, SharedExperienceMarker, TaskStateMarker, HeartbeatMarker, "【记忆库存】" };
        for (int i = h.Count - 1; i >= 0; i--)
            if (h[i].Role == AuthorRole.System && h[i].Content != null
                && markers.Any(mk => h[i].Content!.StartsWith(mk, StringComparison.Ordinal)))
                h.RemoveAt(i);
    }

    // ═══════════ 心绪系统（情绪状态层） ═══════════
    // 原则（Sentipolis/feltstate/emotion-engine 的收敛点）：情绪是显式的持久状态模块，与回复 LLM 分离——
    // fast 层（每轮，零 LLM）从她亲笔的【内心独白】提取 pulse；slow 层（每 N 轮，1 次小调用）判读 PAD；
    // 每轮按半衰期向性格基线回漂。注入块放上下文末尾（内容每轮会变，放尾部不打断主对话的前缀缓存）。
    const string HeartbeatMarker = "[心绪]";
    const string TaskStateMarker = "[当前状态]";
    const string TrailingMarker = "[此刻状态]"; //尾部状态区整条消息的统一头标记（清理逻辑按它识别，含库存行）
    readonly object heartbeatLock = new();
    DesireEngine? desireEngine;
    int heartbeatSlowBase;

    string HeartbeatPath() => ResolveMemoryPath(string.IsNullOrWhiteSpace(Configuration.Heartbeat?.Path) ? "model/心绪.json" : Configuration.Heartbeat!.Path);

    static double BaselineOf(HeartbeatConfig hb, string dim) => dim switch
    {
        "P" => Math.Clamp(hb.BaselineP, -1, 1),
        "A" => Math.Clamp(hb.BaselineA, -1, 1),
        _ => Math.Clamp(hb.BaselineD, -1, 1),
    };

    static double DecayToward(double cur, double baseline, double factor) => baseline + (cur - baseline) * factor;

    /// <summary>提取助手消息中的【内心独白】行（原样保留，不判别是否“情绪”——原材料交给 slow 层判读）。缺失返回 null。</summary>
    static string? ExtractInnerMonologue(string content)
    {
        int i = content.IndexOf("【内心独白】", StringComparison.Ordinal);
        if (i < 0) return null;
        string rest = content[(i + "【内心独白】".Length)..];
        int nl = rest.IndexOf('\n');
        return (nl >= 0 ? rest[..nl] : rest).Trim();
    }

    /// <summary>确保心绪状态文件存在（缺失则创建带性格基线的默认状态；存在则同步基线到最新配置）。</summary>
    void EnsureHeartbeatFile(string path, HeartbeatConfig hb)
    {
        JsonObject root = LoadHeartbeat(path, hb);
        SaveHeartbeat(path, root);
    }

    JsonObject LoadHeartbeat(string path, HeartbeatConfig hb)
    {
        JsonObject root;
        try { root = File.Exists(path) ? JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(path)) ?? new JsonObject() : new JsonObject(); }
        catch { root = new JsonObject(); }

        if (root["mood"] is not JsonObject) root["mood"] = new JsonObject { ["P"] = hb.BaselineP, ["A"] = hb.BaselineA, ["D"] = hb.BaselineD };

        if (root["轨迹"] is not JsonArray) root["轨迹"] = new JsonArray();
        // 基线始终以配置为准（哥哥可调性格基调）
        root["基线"] = new JsonObject { ["P"] = hb.BaselineP, ["A"] = hb.BaselineA, ["D"] = hb.BaselineD };
        return root;
    }

    static void SaveHeartbeat(string path, JsonObject root)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(ModelJsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>fast 层：把本轮【内心独白】原样记入轨迹（原材料完整保留，情绪解读交给 slow 层判读）。无独白则不记录。</summary>
    async Task HeartbeatPulseAsync(HeartbeatConfig hb, string assistantContent)
    {
        string path = HeartbeatPath();
        lock (heartbeatLock)
        {
            JsonObject root = LoadHeartbeat(path, hb);
            string? line = ExtractInnerMonologue(assistantContent);
            if (line == null) { SaveHeartbeat(path, root); return; }
            (root["轨迹"] as JsonArray)!.Add(new JsonObject {
                ["时间"] = DateTime.Now.ToString("MM-dd HH:mm"), ["独白"] = line
            });
            while ((root["轨迹"] as JsonArray)!.Count > 10) (root["轨迹"] as JsonArray)!.RemoveAt(0);
            SaveHeartbeat(path, root);
        }
    }

    /// <summary>slow 层：判读模型读取最近独白轨迹，输出情绪走向段 + PAD 小步更新 + 因果行，写入 [心绪] 块（每 N 轮一次，轮间保持稳定）。</summary>
    async Task HeartbeatSlowJudgeAsync(HeartbeatConfig hb)
    {
        string path = HeartbeatPath();
        string recent, instruction;
        lock (heartbeatLock)
        {
            JsonObject root = LoadHeartbeat(path, hb);
            var traj = root["轨迹"] as JsonArray;
            if (traj == null || traj.Count == 0) return;
            var recentSb = new StringBuilder();
            int n = 0;
            for (int i = traj.Count - 1; i >= 0 && n < 6; i--, n++)
            {
                if (traj[i] is not JsonObject t) continue;
                string mono = t["独白"]?.ToString() ?? ((t["基调"]?.ToString() ?? "") + "：" + (t["感受"]?.ToString() ?? ""));
                if (string.IsNullOrWhiteSpace(mono)) continue;
                recentSb.Append($"[{t["时间"]?.ToString()}] {mono}\n");
            }
            recent = recentSb.ToString();
            instruction = (hb.JudgePrompt ?? "")
                .Replace("{mood}", (root["mood"] as JsonObject)?.ToJsonString() ?? "{}")
                .Replace("{recent}", recent);
        }
        string? raw = null;
        try { raw = await AskCompressModelAsync(instruction, "心绪判读调用失败"); }
        catch (Exception e) { logger.LogWarning(e, "心绪判读调用失败"); return; }
        if (TryParseJsonNode(raw ?? "") is not JsonObject obj)
        {
            logger.LogWarning("心绪判读产出非 JSON，保留当前状态，原始输出：{Raw}", Clip(raw ?? "", 200));
            return;
        }
        lock (heartbeatLock)
        {
            JsonObject cur = LoadHeartbeat(path, hb);
            if (cur["mood"] is not JsonObject mood) { SaveHeartbeat(path, cur); return; }
            foreach (string dim in new[] { "P", "A", "D" })
            {
                double curV = mood[dim]?.GetValue<double>() ?? BaselineOf(hb, dim);
                if (obj[dim] is JsonNode nv && double.TryParse(nv.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double target))
                {
                    target = Math.Clamp(target, -1, 1);
                    double stepped = Math.Clamp(target, curV - 0.25, curV + 0.25); // 小步，防判读模型激进
                    mood[dim] = Math.Round(stepped, 3);
                }
            }
            if (obj["走向"] != null) cur["情绪走向"] = obj["走向"].DeepClone();
            if (obj["因果"] != null) cur["慢层因果"] = obj["因果"].DeepClone();
            if (obj["依据"] != null) cur["慢层依据"] = obj["依据"].DeepClone();
            SaveHeartbeat(path, cur);
        }
        await RefreshTrailingStateAsync();
    }

    static string BuildHeartbeatBlockText(JsonObject root)
    {
        var mood = root["mood"] as JsonObject;
        var sb = new StringBuilder();
        sb.Append(HeartbeatMarker).Append('\n');
        string why = root["慢层因果"]?.ToString() ?? "";
        if (why.Length > 0)
            sb.Append("你最近的情绪走向：").Append(why).Append('\n');
        sb.Append("内在坐标：P=").Append(mood?["P"]?.ToString()).Append("（愉悦）A=").Append(mood?["A"]?.ToString()).Append("（唤醒）D=").Append(mood?["D"]?.ToString()).Append("（支配-顺从）。\n");
        // 身体联动建议：心情显著时给一个表情提示（采不采纳她自决；低落时不硬挂可爱装饰）
        if (double.TryParse(mood?["P"]?.ToString(), out var pv) && double.TryParse(mood?["A"]?.ToString(), out var av))
        {
            string? face = (av, pv) switch
            {
                var (a2, p2) when a2 >= 0.6 && p2 >= 0.5 => "猫耳",
                var (a2, p2) when a2 < 0.35 && p2 >= 0.55 => "头饰",
                _ => null
            };
            if (face != null)
                sb.Append("（此刻心情适合的表情：").Append(face).Append("——挂不挂你说了算）\n");
        }
        sb.Append("【内心独白】从这个状态出发自然演变——它是上一轮的延续，不是每次重新开始；幅度受你的性格约束（深情而克制的病娇），不因“要有情绪”而表演。");
        return sb.ToString();
    }

    /// <summary>中文友好的字符二元组 Jaccard 相似度（0~1）。</summary>
    static double CharBigramJaccard(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        HashSet<string> sa = new();
        for (int i = 0; i < a.Length - 1; i++) sa.Add(a.Substring(i, 2));
        HashSet<string> sb = new();
        for (int i = 0; i < b.Length - 1; i++) sb.Add(b.Substring(i, 2));
        if (sa.Count == 0 || sb.Count == 0) return 0;
        int inter = 0;
        foreach (var g in sa) if (sb.Contains(g)) inter++;
        return (double)inter / (sa.Count + sb.Count - inter);
    }

    // ═══ 触发状态持久化 ═══
    string triggerStatePath = "";
    DateTime semanticLastRun, proceduralLastRun, scenarioLastRun;
    DateTime reflectionLastRun, consistencyLastRun, expIndexLastRun;
    int semanticMsgBase, proceduralMsgBase, scenarioMsgBase;
    int reflectionMsgBase, consistencyMsgBase, expIndexMsgBase;
    /// <summary>共同经历召回块的最近一次刷新时间（节流用）。</summary>
    /// <summary>每个 (文档路径::tier) 独立的消息计数基数，保证各 tier 按各自「轮次条数」触发，互不牵制。</summary>
    readonly Dictionary<string, int> _tierMsgBase = new();
    /// <summary>(文档::tier) 失败后的冷却截止时间：防止巡检每轮重试失败刷新、反复烧 LLM。</summary>
    readonly Dictionary<string, DateTime> _tierRetryAfter = new(StringComparer.Ordinal);
    int generatingExtra; //语义/程序提炼防重入

    void LoadTriggerState()
    {
        try
        {
            if (!File.Exists(triggerStatePath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(triggerStatePath));
            if (d == null) return;
            // 按键容错：单个坏值不再殃及其余字段；解析失败或异常时间戳（<2020 年）一律当作未触发
            foreach (var (key, apply) in new[] {
                ("semantic", (Action<DateTime>)(v => semanticLastRun = v)),
                ("procedural", v => proceduralLastRun = v),
                ("scenario", v => scenarioLastRun = v),
                ("reflection", v => reflectionLastRun = v),
                ("consistency", v => consistencyLastRun = v),
                ("expIndex", v => expIndexLastRun = v),
            })
            {
                if (!d.TryGetValue(key, out var raw)) continue;
                try
                {
                    var parsed = DateTime.FromBinary(raw);
                    if (parsed.Year >= 2020) apply(parsed);
                }
                catch (Exception e) { logger.LogWarning(e, "触发状态 {Key} 值无效，按未触发处理", key); }
            }
        }
        catch (Exception e) { logger.LogWarning(e, "读取触发状态失败"); }
    }

    void SaveTriggerState()
    {
        try
        {
            var d = new Dictionary<string, long> {
                ["semantic"] = semanticLastRun.ToBinary(),
                ["procedural"] = proceduralLastRun.ToBinary(),
                ["scenario"] = scenarioLastRun.ToBinary(),
                ["reflection"] = reflectionLastRun.ToBinary(),
                ["consistency"] = consistencyLastRun.ToBinary(),
                ["expIndex"] = expIndexLastRun.ToBinary(),
            };
            string tmp = triggerStatePath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(d));
            File.Move(tmp, triggerStatePath, overwrite: true);
        }
        catch (Exception e) { logger.LogWarning(e, "写入触发状态失败"); }
    }

    // ═══ 语义/程序记忆摘录持久化 + 注入 ═══
    const string DigestMarker = "[记忆摘录]";
    string SemanticDigestPath => Path.Combine(storagePath, "semantic_digest.txt");
    string ProceduralDigestPath => Path.Combine(storagePath, "procedural_digest.txt");

    /// <summary>
    /// 记忆契约 JSON 校验 + 渲染：程序/语义提示词约定只输出 {"条目":[...]}。
    /// 返回渲染后的短行文本；违反契约、无合法条目或空数组返回 null（保留旧摘录）。
    /// </summary>
    static string? RenderKindDigest(string label, string raw)
    {
        JsonNode? node = TryParseJsonNode(raw ?? "");
        if (node is not JsonObject obj || obj["条目"] is not JsonArray entries)
            return null;

        List<string> lines = new();
        foreach (JsonNode? e in entries)
        {
            if (e is not JsonObject entry) continue;
            string name = entry["名称"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            string rawBody = entry["内容"]?.ToString() ?? entry["步骤"]?.ToString() ?? entry["备注"]?.ToString() ?? "";
            if (ContainsPlaceholder(name) || ContainsPlaceholder(rawBody)) continue;

            if (label == "程序")
            {
                string cond = entry["适用条件"]?.ToString() ?? "";
                var steps = (entry["步骤"] as JsonArray)?
                    .Select(s => (s?.ToString() ?? "").Trim())
                    .Where(s => s.Length > 0).ToList() ?? new List<string>();
                var notes = (entry["注意事项"] as JsonArray)?
                    .Select(s => (s?.ToString() ?? "").Trim())
                    .Where(s => s.Length > 0).ToList() ?? new List<string>();
                string body = steps.Count > 0 ? string.Join("→", steps) : rawBody;
                if (string.IsNullOrWhiteSpace(body)) continue;
                string line = $"- {name}" + (cond.Length > 0 ? $"（适用：{cond}）" : "") + $"：{body}";
                if (notes.Count > 0) line += "；注意：" + string.Join("；", notes);
                lines.Add(line);
            }
            else
            {
                string content = rawBody;
                if (string.IsNullOrWhiteSpace(content)) continue;
                lines.Add($"- {name}：{content}");
            }
        }
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    void SaveDigestFile(string label, string summary)
    {
        string path = label == "语义" ? SemanticDigestPath : ProceduralDigestPath;
        File.WriteAllText(path, summary.Trim());
    }

    string BuildDigestText()
    {
        string sem = File.Exists(SemanticDigestPath) ? File.ReadAllText(SemanticDigestPath).Trim() : "";
        string pro = File.Exists(ProceduralDigestPath) ? File.ReadAllText(ProceduralDigestPath).Trim() : "";
        var sb = new System.Text.StringBuilder();
        if (sem.Length > 0) sb.Append("[语义记忆]\n").Append(RenderAsFirstPerson(sem)).Append('\n');
        if (pro.Length > 0) sb.Append("[程序记忆]\n").Append(RenderAsFirstPerson(pro)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    Task RefreshDigestInjectionAsync()
    {
        if (!Configuration.DigestInject) return Task.CompletedTask;
        string digest = BuildDigestText();
        if (digest.Length == 0) return Task.CompletedTask;
        string blockText = $"{DigestMarker}\n{digest}";
        ChatBot.EditChatHistory(thread => {
            var h = thread.ChatHistory;
            // 先查现有块：内容一致就整体跳过——无变化的写入会打断 UI 渲染（枚举中改列表会让界面崩溃）
            for (int i = 0; i < h.Count; i++)
                if (h[i].Role == AuthorRole.System && h[i].Content == blockText)
                    return;
            for (int i = h.Count - 1; i >= 0; i--)
                if (h[i].Role == AuthorRole.System && h[i].Content != null && h[i].Content!.Contains(DigestMarker))
                { h.RemoveAt(i); break; }
            int idx = 0;
            while (idx < h.Count && h[idx].Role == AuthorRole.System) idx++;
            h.Insert(Math.Min(idx, h.Count), new ChatMessageContent(AuthorRole.System, blockText));
        }, "注入记忆摘录");
        return Task.CompletedTask;
    }

    static string Clip(string s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + "…");

    /// <summary>
    /// 解析用于压缩的语言模型：若配置了类名且类型存在，则从容器中获取，否则回退到默认语言模型。
    /// </summary>
    async Task<ILanguageModel> ResolveCompressModel()
    {
        string className = Configuration.CompressModelClassName;
        if (string.IsNullOrWhiteSpace(className))
            return languageModel;

        Type? modelType = moduleSystem.GetAllModules()
            .FirstOrDefault(type => type.Name == className || type.FullName == className);
        if (modelType == null)
        {
            logger.LogWarning("未找到压缩语言模型类型“{ClassName}”，将使用默认语言模型。", className);
            return languageModel;
        }

        try
        {
            object instance = await ChatActivity.Container.RequireInstance(modelType, true);
            if (instance is ILanguageModel model)
                return model;

            logger.LogWarning("类型“{ClassName}”未实现 {Interface}，将使用默认语言模型。",
                className, nameof(ILanguageModel));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "获取压缩语言模型“{ClassName}”失败，将使用默认语言模型。", className);
        }

        return languageModel;
    }
    protected override async Task OnDestroy()
    {
        ChatBot.ChatSend -= OnChatSend;
        ChatBot.ChatHistoryAdd -= OnChatHistoryAdd;
        tokenUser.TokenUsed -= OnTokenUsed;
        if (vectorizer != null) await vectorizer.DisposeAsync(); // OnStart 完成前被卸载时 vectorizer 尚未初始化
    }

    string OnChatSend(string message)
    {
        // 发请求前先清理助手上轮可能产生的重复 <!----> 标记（11-04 一次 48k 输出事故的残骸），避免它每轮随上下文重发。
        SanitizeAssistantMarkerSpam();
        foreach (string keyword in Configuration.RecallHintKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return $"{message}\n(提示：如有需要，可以使用<{nameof(MemoryService)}>来尝试回忆往事)";
            }
        }
        return message;
    }

    /// <summary>清理助手消息里由格式纠正触发的重复 &lt;!----&gt; 标记；零 LLM，只压连续≥3个的串，保留正文。</summary>
    void SanitizeAssistantMarkerSpam()
    {
        try
        {
            bool changed = false;
            ChatBot.EditChatHistory(thread => {
                foreach (var m in thread.ChatHistory)
                {
                    if (m.Role != AuthorRole.Assistant || string.IsNullOrEmpty(m.Content)) continue;
                    string c = m.Content;
                    if (!c.Contains("<!---->", StringComparison.Ordinal)) continue;
                    string cleaned = System.Text.RegularExpressions.Regex
                        .Replace(c, @"(?:<!---->\s*){3,}", "<!---->")
                        .TrimEnd();
                    if (cleaned != c) { m.Content = cleaned; changed = true; }
                }
                if (changed) memoryManager.SaveHistory(thread.ChatHistory);
            }, "清理助手重复标记");
            if (changed) logger.LogInformation("[记忆][清理] 已折叠助手消息中的重复 <!----> 标记");
        }
        catch (Exception e) { logger.LogWarning(e, "[记忆][清理] 助手标记清理失败"); }
    }
    async void OnChatHistoryAdd(ChatMessageContent content)
    {
        try
        {
            lastHistoryActivity = DateTime.Now;
            if (content.Role == AuthorRole.User) desireEngine?.OnUserMessage(content.Content ?? "");
            if (Interlocked.Exchange(ref compressing, 1) == 1)
                return; //已在压缩中，跳过本次留待下轮触发

            if (content.Role != AuthorRole.Assistant)
                return; //只在ai说话后整理，这样对话更完整，而且可以避免在ai异常时保持记忆

            //阶段一：先落盘历史并取出快照，随后在快照副本上完成压缩检测与LLM归纳，避免长时间占用真实上下文
            MemoryCompressionPlan? plan;
            ChatHistory snapshot = new();
            await ChatBot.EditChatHistoryAsync(thread => {
                memoryManager.SaveHistory(thread.ChatHistory);
                foreach (var message in thread.ChatHistory)
                    snapshot.Add(message);
                return Task.CompletedTask;
            }, "存储记忆");

            var thinking = Configuration.ThinkingCompress
                ? compressModel.GetThinkingRequester().Rent("记忆压缩")
                : compressModel.GetThinkingRequester().Rent("记忆压缩(轻量)");
            try
            {
                plan = await memoryManager.Analyze(snapshot);
                if (plan != null)
                {
                    // 只打标量+摘要开头：整个 plan 的 ToString 会带上全部快照消息/锚点/完整摘要，一次几千行
                    string summaryHead = plan.Summary.Replace('\n', ' ');
                    logger.LogInformation("[记忆] 压缩计划：快照 {N} 条（内容 {C} 条），Insert(#{I}) 删{R}条 升L{L} [{S:MM-dd HH:mm}→{E:HH:mm}] 摘要前100字：{H}",
                        snapshot.Count, snapshot.Count(m => m.Role != AuthorRole.System),
                        plan.InsertIndex, plan.RemoveCount, plan.NewLevel, plan.StartTime, plan.EndTime,
                        summaryHead.Length > 100 ? summaryHead[..100] : summaryHead);
                }
            }
            finally
            {
                thinking.Dispose();
            }

            //阶段二：归纳完成后，原子替换真实上下文
            if (plan != null)
            {
                await ChatBot.EditChatHistoryAsync(async thread => {
                    await memoryManager.Apply(thread.ChatHistory, plan);
                    // 完整性哨兵：压缩应用后立即核对账面；异常则跳过落盘（坏账不固化）并报警——运行时可从镜像找回或重启恢复
                    var (ok, reason) = QuickIntegrityCheck(thread.ChatHistory);
                    if (!ok)
                    {
                        logger.LogError("[记忆][完整性] 压缩计划应用后覆盖异常，本次不落盘：{Reason}", reason);
                        interactor.Poke($"⚠️ 记忆完整性哨兵拦截了一次压缩落盘：{reason}\n上下文暂被修改但未写入磁盘，重启或重新激活即可从磁盘恢复。");
                        return;
                    }
                    memoryManager.SaveHistory(thread.ChatHistory);
                }, "应用记忆压缩");
            }

            // 心绪 fast 层：从本轮【内心独白】提取（零 LLM）
            var hbc = Configuration.Heartbeat;
            if (hbc is { Enable: true })
            {
                try { await HeartbeatPulseAsync(hbc, content.Content ?? ""); }
                catch (Exception e) { logger.LogWarning(e, "心绪 pulse 更新失败"); }
            }

            // 尾部状态区：[共同经历] + [当前状态]（任务层） + [心绪] 合成一条系统消息挂在末尾——纯本地计算，变化才动列表
            try { await RefreshTrailingStateAsync(); }
            catch (Exception e) { logger.LogWarning(e, "尾部状态区刷新失败"); }
            try { ApplyFoldInHistory("压缩后"); }
            catch (Exception e) { logger.LogWarning(e, "压缩后折叠渲染失败"); }
            try { SnapshotHistoryDaily(); }
            catch (Exception e) { logger.LogWarning(e, "History 快照失败"); }
        }
        catch (Exception e)
        {
            logger.LogError(e, "记忆压缩失败");
        }
        finally
        {
            Interlocked.Exchange(ref compressing, 0);
        }
    }

    TokenCostSentinel? tokenSentinel;
    /// <summary>上一轮主对话实测输入 token（蒸馏/压缩/活文档刷新等内部调用不计）——总上下文预算的观测值。</summary>
    long lastMainInputTokens;
    /// <summary>上次折叠时统计的上下文总字符——请求应有大小的估算基准（×0.62≈token）。</summary>
    long lastContextChars;
    DateTime lastContextLossAlarm;

    void OnTokenUsed(TokenUsageContext ctx)
    {
        TokenUsage usage = ctx.TokenUsage;
        // 每次LLM调用（主对话/蒸馏/压缩）在日志留一笔用量，供 alife.log 直接观测（看板之外的双保险）
        string scene = (ctx.UserMessage ?? "").Replace('\n', ' ');
        logger.LogInformation("[Token] in={Input} cached={Cached} out={Output} total={Total} | {Scene}",
            usage.Input, usage.Cached, usage.Output, usage.Total,
            scene.Length > 40 ? scene[..40] : scene);
        // 主对话实测输入 token（总上下文预算用）：蒸馏/压缩/活文档等内部调用的输入（单笔可达 9 万）不计入
        string sceneTrim = scene.TrimStart();
        bool distillScene = sceneTrim.StartsWith("我是阿宵", StringComparison.Ordinal)
            || sceneTrim.StartsWith("【记忆系统输出契约】", StringComparison.Ordinal)
            || sceneTrim.StartsWith("【来自记忆系统的信息】", StringComparison.Ordinal)
            || sceneTrim.StartsWith("记忆压缩", StringComparison.Ordinal);
        if (!distillScene)
        {
            // 网关失败/取消/SSL 中断也会发一笔 0 用量；不能把它当成“上下文丢失”，否则会误报并误导她。
            if (usage.Input > 0) Interlocked.Exchange(ref lastMainInputTokens, usage.Input);
            // 上下文哨兵：主对话请求 token 远小于装配上下文的估算（system 消息/早期内容被裁掉时触发）。
            // 11-04 实测：框架在消息数超阈值时把 system 层裁出请求，输入从 4.9w 掉到 2.5w，阿宵确认看不到角色卡/活文档。
            long chars = Interlocked.Read(ref lastContextChars);
            if (usage.Input > 0 && chars > 8000)
            {
                double expected = chars * 0.62; //中文混合内容经验比值
                if (usage.Input < expected * 0.5 && (DateTime.Now - lastContextLossAlarm).TotalHours >= 1)
                {
                    lastContextLossAlarm = DateTime.Now;
                    logger.LogError("[记忆][上下文哨兵] 主对话请求仅 {Input} token，装配上下文估算约 {Expected} token（{Chars} 字符）——疑似 system 消息/早期上下文没进请求",
                        usage.Input, (int)expected, chars);
                    interactor.Poke($"⚠️ 上下文哨兵：本轮请求只有 {usage.Input} token，装配上下文估算约 {(int)expected}——system 消息疑似被裁掉。先重启角色试试，反复出现就报框架作者。");
                }
            }
        }
        // 费用哨兵：持续异常增长 + 前缀缓存连续未命中（全零 LLM，本地比对；2026-11-02 尾部块膨胀事故的泛化）
        try
        {
            tokenSentinel ??= new TokenCostSentinel(Configuration.TokenGrowthAlarmPerTurn, Configuration.TokenGrowthWindow, Configuration.CacheMissAlarmTurns);
            if (usage.Input > 0 && tokenSentinel.Feed(usage.Input, usage.Cached) is { } alarm)
            {
                logger.LogError("[记忆][费用哨兵] {Channel}：{Reason}", alarm.Channel, alarm.Reason);
                interactor.Poke($"⚠️ token 费用哨兵（{alarm.Channel}）：{alarm.Reason}");
            }
        }
        catch (Exception e) { logger.LogWarning(e, "token 费用哨兵失败"); }

        if (usage.Total < Configuration.TokenWarningThreshold)
            return;

        logger.LogWarning($$"""
                            检测到上下文token已超过警告线，必须进行人工干涉！
                            (当前上下文约 {{usage.Input}} tokens / 警告线 {{Configuration.TokenWarningThreshold}} tokens)
                            1. 检查完整背景中的永久（最高级）记忆，此记忆通常由AI自主记忆，必须由人工删除。
                            2. 如果自动压缩的记忆已达最高级，而造成记忆膨胀，请继续提高最大记忆压缩等级。
                            3. 如果你的模型实际上还有充足的token容量，且未觉得上下文异常，可以提高警告线数值。
                            """);
    }

    /// <summary>
    /// token 费用哨兵（纯本地状态机，零 LLM）。两条独立通道：
    /// ① 持续异常增长——观察窗口内「最小」轮增超阈值才报警：发图/大工具结果的一次性大增量不会误报，
    ///    「每轮重复注入」形态（如尾部块清理失效）必然触发；压缩导致的负增长会重置窗口。
    /// ② 前缀缓存连续未命中——稳态应 85-99% 命中，连续多轮 Cached=0 说明中转站缓存失效或前缀被反复改动。
    /// 每条通道报警一次后静默，恢复后重新武装（同因不轰炸）。
    /// </summary>
    internal sealed class TokenCostSentinel(int growthThreshold, int window, int cacheMissTurns)
    {
        readonly Queue<int> growth = new();
        readonly Queue<bool> miss = new();
        long lastInput = -1;
        bool growthAlarming, missAlarming;

        public (string Channel, string Reason)? Feed(long input, long cached)
        {
            if (lastInput >= 0)
            {
                long d = input - lastInput;
                if (d < -5000) { growth.Clear(); miss.Clear(); } // 压缩/清理导致的缩水：重置观测窗口
                else
                {
                    growth.Enqueue((int)d);
                    while (growth.Count > Math.Max(2, window)) growth.Dequeue();
                    miss.Enqueue(cached == 0);
                    while (miss.Count > Math.Max(2, cacheMissTurns)) miss.Dequeue();
                }
            }
            lastInput = input;

            if (growthThreshold > 0 && growth.Count >= window && growth.Min() > growthThreshold)
            {
                if (!growthAlarming)
                {
                    growthAlarming = true;
                    return ("token增长", $"连续 {growth.Count} 轮输入每轮最少增长 {growth.Min()} token（阈值 {growthThreshold}）——疑似有内容在每轮重复注入；若当时在连续发图或跑大工具属正常，恢复后会自动静默");
                }
                return null;
            }
            growthAlarming = false;

            if (cacheMissTurns > 0 && miss.Count >= cacheMissTurns && miss.All(m => m) && input > 20000)
            {
                if (!missAlarming)
                {
                    missAlarming = true;
                    return ("缓存未命中", $"连续 {miss.Count} 轮前缀缓存 0 命中（输入 {input} token）——中转站缓存可能失效或前缀被反复改动");
                }
                return null;
            }
            missAlarming = false;
            return null;
        }
    }
}

/// <summary>
/// 感知上下文的人设化压缩器
/// </summary>
class AlifeHistoryCompressor(
    ILanguageModel languageModel,
    float probability,
    string promptTemplate,
    ITokenUser tokenUser,
    int maxOutputChars) :
    HistoryCompressor
{
    public override async Task<string?> Compress(ChatHistoryAgentThread chatHistoryAgentThread, string range)
    {
        if (Random.Shared.NextSingle() > probability)
            return null;

        ChatHistory history = chatHistoryAgentThread.ChatHistory;
        int startIndex = history.Count;
        string prompt = promptTemplate.Replace("{range}", range);
        try
        {
            history.AddUserMessage(prompt);

            AlifeLog.LogInformation("记忆压缩中......");
            // 5.2.1：返回 ChatResult，异常与用量从回调改为结果对象/ITokenUser 事件
            TokenUsageContext? usedCtx = null;
            void Capture(TokenUsageContext c) => usedCtx = c;
            Action<TokenUsageContext> capture = Capture;
            ChatResult chatResult;
            try
            {
                tokenUser.TokenUsed += capture;
                chatResult = await languageModel.ChatStreamingAsync(chatHistoryAgentThread);
            }
            finally { tokenUser.TokenUsed -= capture; }
            AlifeLog.LogInformation("压缩消耗：" + (usedCtx.HasValue ? usedCtx.Value.TokenUsage.ToString() : "n/a"));
            string response = chatResult.AIMessage;
            Exception? exception = chatResult.Exception;

            if (exception != null)
                throw new Exception("记忆压缩失败！", innerException: exception);
            if (string.IsNullOrEmpty(response) || response.Length < 100)
                throw new Exception($"记忆压缩失败！压缩结果长度过短：“{response}”");
            // 截断启发式：摘要必须含两段结构标题，且不能悬在半行上（09-02 的 L6 就是输出被截在事件概述"2."处，
            // 当时长度守卫≥100 拦不住）。拒绝后本轮不压缩，下一轮巡检自然重试（重新生成大概率完整）。
            if (!response.Contains("键值数据") || !response.Contains("事件概述"))
                throw new Exception($"记忆压缩失败！产物缺少结构段（键值数据/事件概述），疑似截断：“{response[..Math.Min(120, response.Length)]}”");
            var lastLine = response.Split('\n').Last(l => !string.IsNullOrWhiteSpace(l)).Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(lastLine, @"^\d{1,3}[\.、，]?$") || lastLine.Length < 4)
                throw new Exception($"记忆压缩失败！产物末行悬空（{lastLine}），疑似输出被截断");

            return response;
        }
        finally
        {
            history.RemoveRange(startIndex, history.Count - startIndex);
        }
    }
}
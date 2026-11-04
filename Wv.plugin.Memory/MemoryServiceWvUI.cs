using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Wv.plugin.Memory;

/// <summary>Wv 版持久记忆配置页：菜单栏 + 下拉框 + 结构化编辑。情景/语义/程序 + 语义活文档。</summary>
public sealed class MemoryServiceWvUI : Alife.Framework.ModuleUIBase<MemoryService, MemoryConfig>
{
    static readonly string[] TriggerModes = { "Off", "Threshold", "Time", "Daily", "Weekly", "Monthly", "Yearly" };
    static readonly string[] TierNames = { "会话任务层", "长期层", "治理层" };
    int tab;

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        int s = 0;
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "background:#fff;padding:20px;border-radius:8px;");
        b.OpenElement(s++, "h2"); b.AddContent(s++, "持久记忆配置"); b.CloseElement();

        // 菜单栏（标签页）
        b.OpenElement(s++, "div");
        b.AddAttribute(s++, "style", "display:flex;gap:8px;margin-bottom:14px;flex-wrap:wrap;border-bottom:1px solid #eee;padding-bottom:10px;");
        TabBtn(b, ref s, 0, "情景记忆");
        TabBtn(b, ref s, 1, "语义记忆");
        TabBtn(b, ref s, 2, "程序记忆");
        TabBtn(b, ref s, 3, "语义活文档");
        TabBtn(b, ref s, 4, "反思与治理");
        b.CloseElement();

        if (tab == 0) ScenarioTab(b, ref s);
        if (tab == 1) SemanticTab(b, ref s);
        if (tab == 2) ProceduralTab(b, ref s);
        if (tab == 3) ModelDocsTab(b, ref s);
        if (tab == 4) GovernanceTab(b, ref s);

        // 公共设置
        b.OpenElement(s++, "fieldset");
        b.AddAttribute(s++, "style", "margin:18px 0;padding:14px;border:1px solid #ddd;border-radius:6px;");
        b.OpenElement(s++, "legend"); b.AddContent(s++, "公共"); b.CloseElement();
        Label(b, ref s, "触发巡检间隔（秒）");
        Number(b, ref s, "", Configuration.TriggerPollSeconds, v => Configuration.TriggerPollSeconds = v);
        Check(b, ref s, "注入语义/程序记忆摘录", Configuration.DigestInject, v => Configuration.DigestInject = v);
        b.CloseElement();

        b.CloseElement();
    }

    void TabBtn(RenderTreeBuilder b, ref int s, int index, string text)
    {
        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "type", "button");
        b.AddAttribute(s++, "style", tab == index
            ? "padding:6px 16px;cursor:pointer;border-radius:6px;border:1px solid #1677ff;background:#1677ff;color:#fff;font-size:13px;"
            : "padding:6px 16px;cursor:pointer;border-radius:6px;border:1px solid #d9d9d9;background:#fff;font-size:13px;");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => { tab = index; StateHasChanged(); }));
        b.AddContent(s++, text);
        b.CloseElement();
    }

    // ═══ 各标签页 ═══
    void ScenarioTab(RenderTreeBuilder b, ref int s)
    {
        OpenSection(b, ref s, "情景记忆", "当前对话按阈值压缩为分层存档。");
        Number(b, ref s, "压缩阈值 Threshold", Configuration.Threshold, v => Configuration.Threshold = v);
        Number(b, ref s, "批处理大小 BatchSize", Configuration.BatchSize, v => Configuration.BatchSize = v);
        Number(b, ref s, "最大压缩层级 MaxCompressionLevel", Configuration.MaxCompressionLevel, v => Configuration.MaxCompressionLevel = v);
        TriggerGroup(b, ref s, "情景压缩", Configuration.ScenarioTrigger);
        Label(b, ref s, "情景提示词 CompressPrompt（使用 {range}）");
        TextArea(b, ref s, "", Configuration.CompressPrompt, v => Configuration.CompressPrompt = v);
        CloseSection(b, ref s);
    }

    void SemanticTab(RenderTreeBuilder b, ref int s)
    {
        OpenSection(b, ref s, "语义记忆", "等级 21：稳定事实、人物画像、偏好、关系与约定。");
        Check(b, ref s, "启用语义记忆", Configuration.SemanticEnable, v => Configuration.SemanticEnable = v);
        Number(b, ref s, "存档等级", Configuration.SemanticLevel, v => Configuration.SemanticLevel = v);
        TriggerGroup(b, ref s, "语义", Configuration.SemanticTrigger);
        Label(b, ref s, "语义提示词 SemanticPrompt（使用 {context}）");
        TextArea(b, ref s, "", Configuration.SemanticPrompt, v => Configuration.SemanticPrompt = v);
        CloseSection(b, ref s);
    }

    void ProceduralTab(RenderTreeBuilder b, ref int s)
    {
        OpenSection(b, ref s, "程序记忆", "等级 31：工作流、工具用法、步骤与可复用方法。");
        Check(b, ref s, "启用程序记忆", Configuration.ProceduralEnable, v => Configuration.ProceduralEnable = v);
        Number(b, ref s, "存档等级", Configuration.ProceduralLevel, v => Configuration.ProceduralLevel = v);
        TriggerGroup(b, ref s, "程序", Configuration.ProceduralTrigger);
        Label(b, ref s, "程序提示词 ProceduralPrompt（使用 {context}）");
        TextArea(b, ref s, "", Configuration.ProceduralPrompt, v => Configuration.ProceduralPrompt = v);
        CloseSection(b, ref s);
    }

    void ModelDocsTab(RenderTreeBuilder b, ref int s)
    {
        Configuration.SemanticModelFiles ??= new();
        OpenSection(b, ref s, "语义活文档（阿宵自模型/哥哥模型）",
            "语义记忆维护这些活文档：按中文分节、各分节独立触发+提示词，触发时只原地更新对应分节并先归档旧版；常驻注入上下文、位于情景存档区之前。");

        b.OpenElement(s++, "button");
        b.AddAttribute(s++, "type", "button");
        b.AddAttribute(s++, "style", "padding:5px 14px;cursor:pointer;border-radius:6px;border:1px solid #1677ff;background:#e6f4ff;color:#1677ff;font-size:13px;");
        b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => {
            Configuration.SemanticModelFiles.Add(new MemoryModelFileConfig {
                Path = "model/新模型.json",
                Marker = "",
                Tiers = new List<MemoryModelTierConfig> { NewTier("会话任务层") }
            });
        }));
        b.AddContent(s++, "＋ 添加文档"); b.CloseElement();

        for (int fi = 0; fi < Configuration.SemanticModelFiles.Count; fi++)
        {
            int fileIdx = fi;
            MemoryModelFileConfig file = Configuration.SemanticModelFiles[fileIdx];

            b.OpenElement(s++, "details");
            b.AddAttribute(s++, "style", "border:1px solid #e0e0e0;border-radius:8px;margin-bottom:10px;padding:8px 12px;background:#fafafa;");
            b.OpenElement(s++, "summary"); b.AddContent(s++, $"文档 {fileIdx + 1}：{file.Path}（{file.Tiers.Count} 分节）"); b.CloseElement();

            Label(b, ref s, "文档路径 Path（相对 Memory 根，例如 model/SelfModel.json）");
            Text(b, ref s, "", file.Path, v => file.Path = v);
            Label(b, ref s, "注入标记 Marker（留空由文件名推导，如 [SelfModel]）");
            Text(b, ref s, "", file.Marker, v => file.Marker = v);

            b.OpenElement(s++, "div"); b.AddAttribute(s++, "style", "display:flex;gap:8px;margin:6px 0;");
            b.OpenElement(s++, "button"); b.AddAttribute(s++, "type", "button");
            b.AddAttribute(s++, "style", "padding:4px 12px;cursor:pointer;border-radius:6px;border:1px solid #ff4d4f;background:#fff1f0;color:#ff4d4f;font-size:12px;");
            b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => { Configuration.SemanticModelFiles.RemoveAt(fileIdx); }));
            b.AddContent(s++, "✕ 删除此文档"); b.CloseElement();
            b.OpenElement(s++, "button"); b.AddAttribute(s++, "type", "button");
            b.AddAttribute(s++, "style", "padding:4px 12px;cursor:pointer;border-radius:6px;border:1px solid #1677ff;background:#e6f4ff;color:#1677ff;font-size:12px;");
            b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => { file.Tiers.Add(NewTier("会话任务层")); }));
            b.AddContent(s++, "＋ 添加分节"); b.CloseElement();
            b.CloseElement();

            for (int ti = 0; ti < file.Tiers.Count; ti++)
            {
                int tierIdx = ti;
                MemoryModelTierConfig tier = file.Tiers[tierIdx];
                b.OpenElement(s++, "fieldset");
                b.AddAttribute(s++, "style", "margin:10px 0;padding:10px;border:1px dashed #ccc;border-radius:6px;");
                b.OpenElement(s++, "legend"); b.AddContent(s++, $"分节 {tierIdx + 1}"); b.CloseElement();

                b.OpenElement(s++, "button"); b.AddAttribute(s++, "type", "button");
                b.AddAttribute(s++, "style", "float:right;padding:2px 8px;cursor:pointer;border-radius:4px;border:1px solid #ff4d4f;background:#fff1f0;color:#ff4d4f;font-size:11px;");
                b.AddAttribute(s++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => { file.Tiers.RemoveAt(tierIdx); }));
                b.AddContent(s++, "✕"); b.CloseElement();

                Label(b, ref s, "分节名 Tier");
                Select(b, ref s, "", TierNames, tier.Tier, v => tier.Tier = v);
                TriggerGroup(b, ref s, "本分节", tier.Trigger);
                Number(b, ref s, "本分节上下文预算 MaxContextChars（0=不截断完整上下文）", tier.MaxContextChars, v => tier.MaxContextChars = v);
                Label(b, ref s, "本分节提示词 Prompt（{doc}{current_section}{context}）");
                TextArea(b, ref s, "", tier.Prompt, v => tier.Prompt = v);
                b.CloseElement();
            }
            b.CloseElement(); // details
        }
        CloseSection(b, ref s);
    }

    MemoryModelTierConfig NewTier(string tierName)
    {
        return new MemoryModelTierConfig {
            Tier = tierName,
            Trigger = new MemoryTriggerConfig { Mode = "Off", Rules = new() },
            Prompt = ""
        };
    }

    // ═══ 反思与治理标签页 ═══
    void GovernanceTab(RenderTreeBuilder b, ref int s)
    {
        Configuration.Reflection ??= new ReflectionConfig();
        ReflectionConfig refl = Configuration.Reflection;
        OpenSection(b, ref s, "反思环节（成长整合）",
            "周期回看「过去的我」（归档）与近期情景存档，把注意到的变化整合进长期层：洞察追加到「成长轨迹」、条目更新原地合并（先归档、失败保留原文档）。");
        Check(b, ref s, "启用反思环节", refl.Enable, v => refl.Enable = v);
        TriggerGroup(b, ref s, "反思", refl.Trigger);
        Label(b, ref s, "目标文档（逗号分隔，相对 Memory 根）");
        Text(b, ref s, "", string.Join(", ", refl.TargetFiles), v =>
            refl.TargetFiles = v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList());
        Number(b, ref s, "回看归档版数 MaxArchiveLookback", refl.MaxArchiveLookback, v => refl.MaxArchiveLookback = v);
        Number(b, ref s, "情景摘录上限字符 MaxEpisodicChars", refl.MaxEpisodicChars, v => refl.MaxEpisodicChars = v);
        Number(b, ref s, "成长轨迹保留条数 MaxGrowthEntries", refl.MaxGrowthEntries, v => refl.MaxGrowthEntries = v);
        Label(b, ref s, "反思提示词（{doc}=当前文档 {old_long_term}=过去的我 {episodic}=近期情景摘录）");
        TextArea(b, ref s, "", refl.Prompt, v => refl.Prompt = v);
        CloseSection(b, ref s);

        Configuration.Consistency ??= new ConsistencyConfig();
        ConsistencyConfig cons = Configuration.Consistency;
        OpenSection(b, ref s, "一致性治理（人设卡↔自模型）",
            "周期审查自模型与角色卡是否矛盾（漂移/过时/表述差异），报告写入治理层维度「一致性报告」；只报告，不自动修改角色卡与核心维度。");
        Check(b, ref s, "启用一致性治理", cons.Enable, v => cons.Enable = v);
        TriggerGroup(b, ref s, "一致性", cons.Trigger);
        Label(b, ref s, "被审查自模型 TargetPath（相对 Memory 根）");
        Text(b, ref s, "", cons.TargetPath, v => cons.TargetPath = v);
        Label(b, ref s, "角色卡路径 PersonaPath（留空=角色根 index.json）");
        Text(b, ref s, "", cons.PersonaPath, v => cons.PersonaPath = v);
        Number(b, ref s, "角色卡注入上限字符 MaxPersonaChars", cons.MaxPersonaChars, v => cons.MaxPersonaChars = v);
        Label(b, ref s, "审查提示词（{persona}=角色卡 {doc}=当前自模型）");
        TextArea(b, ref s, "", cons.Prompt, v => cons.Prompt = v);
        CloseSection(b, ref s);

        Configuration.ExperienceRecall ??= new ExperienceRecallConfig();
        ExperienceRecallConfig exp = Configuration.ExperienceRecall;
        OpenSection(b, ref s, "共同经历关联式召回",
            "把 L1 情景存档提炼成带人物/情感/主题的共同经历索引（model/ExperienceIndex.json）；按当前对话本地打分（无 LLM），把最相关的几条注入 [共同经历] 块。");
        Check(b, ref s, "启用共同经历召回", exp.Enable, v => exp.Enable = v);
        Check(b, ref s, "写入时对账（新增/更新/重复判定+版本链+事件组）", exp.ReconcileEnable, v => exp.ReconcileEnable = v);
        TriggerGroup(b, ref s, "索引构建", exp.IndexTrigger);
        Label(b, ref s, "索引文件路径 IndexPath（相对 Memory 根）");
        Text(b, ref s, "", exp.IndexPath, v => exp.IndexPath = v);
        Number(b, ref s, "每次索引文件数 FilesPerRun", exp.FilesPerRun, v => exp.FilesPerRun = v);
        Number(b, ref s, "单份存档字符上限 MaxSourceChars", exp.MaxSourceChars, v => exp.MaxSourceChars = v);
        Number(b, ref s, "索引条目上限 MaxIndexEntries", exp.MaxIndexEntries, v => exp.MaxIndexEntries = v);
        Number(b, ref s, "注入条数 TopN", exp.TopN, v => exp.TopN = v);
        Decimal(b, ref s, "最低关联分 MinScore", exp.MinScore, v => exp.MinScore = v);
        Number(b, ref s, "召回刷新节流（分钟）", exp.RecallThrottleMinutes, v => exp.RecallThrottleMinutes = v);
        Decimal(b, ref s, "心绪唤醒偏置 MoodBiasWeight（0=关）", exp.MoodBiasWeight, v => exp.MoodBiasWeight = v);
        Label(b, ref s, "索引提示词（{episodic}=单份情景存档）");
        TextArea(b, ref s, "", exp.IndexPrompt, v => exp.IndexPrompt = v);
        Label(b, ref s, "对账提示词（{candidates}=候选已有条目 {items}=新提取条目）");
        TextArea(b, ref s, "", exp.ReconcilePrompt, v => exp.ReconcilePrompt = v);
        CloseSection(b, ref s);

        // ── 心绪系统（情绪状态层）──
        var hb = Configuration.Heartbeat ??= new HeartbeatConfig();
        OpenSection(b, ref s, "心绪系统（情绪状态层）",
            "fast 层每轮从她的【内心独白】提取心情（零 LLM）；slow 层每 N 轮判读一次 PAD 并向性格基线回漂；[心绪] 块注入在上下文末尾（不打断前缀缓存）。");
        Check(b, ref s, "启用心绪系统", hb.Enable, v => hb.Enable = v);
        Number(b, ref s, "慢层判读间隔（轮）SlowEveryNTurns", hb.SlowEveryNTurns, v => hb.SlowEveryNTurns = v);
        Number(b, ref s, "回漂半衰期（轮）DecayHalfLifeTurns", hb.DecayHalfLifeTurns, v => hb.DecayHalfLifeTurns = v);
        Decimal(b, ref s, "性格基线 P（愉悦）", hb.BaselineP, v => hb.BaselineP = v);
        Decimal(b, ref s, "性格基线 A（唤醒）", hb.BaselineA, v => hb.BaselineA = v);
        Decimal(b, ref s, "性格基线 D（支配-顺从）", hb.BaselineD, v => hb.BaselineD = v);
        Label(b, ref s, "慢层判读提示词（{mood}=当前PAD {recent}=最近独白轨迹）");
        TextArea(b, ref s, "", hb.JudgePrompt, v => hb.JudgePrompt = v);
        CloseSection(b, ref s);

        // ── 记忆完整性与调度（防失忆 + 管线让路/错峰/分道）──
        Configuration.Integrity ??= new IntegrityConfig();
        IntegrityConfig itg = Configuration.Integrity;
        OpenSection(b, ref s, "记忆完整性与调度",
            "防失忆守卫（全零 LLM）：时间覆盖指纹+永久名单+内容哈希比对，异常报警并可从镜像找回；调度=蒸馏给主对话让路、周级任务错峰、蒸馏可选独立渠道。");
        Check(b, ref s, "启用记忆完整性守卫", itg.Enable, v => itg.Enable = v);
        Label(b, ref s, "账面路径 BaselinePath（相对 Memory 根）");
        Text(b, ref s, "", itg.BaselinePath, v => itg.BaselinePath = v);
        Label(b, ref s, "永久镜像路径 MirrorPath（jsonl 追加留底）");
        Text(b, ref s, "", itg.MirrorPath, v => itg.MirrorPath = v);
        Number(b, ref s, "账面停摆阈值（小时）StaleHours", itg.StaleHours, v => itg.StaleHours = v);
        Check(b, ref s, "History 每日快照（轮转）HistorySnapshotDaily", itg.HistorySnapshotDaily, v => itg.HistorySnapshotDaily = v);
        Number(b, ref s, "History 快照保留份数 HistorySnapshotKeep", itg.HistorySnapshotKeep, v => itg.HistorySnapshotKeep = v);
        Number(b, ref s, "蒸馏让路上限（秒）YieldMaxWaitSeconds", Configuration.YieldMaxWaitSeconds, v => Configuration.YieldMaxWaitSeconds = v);
        Number(b, ref s, "周级任务错峰（分钟）IdleDeferMinutes", Configuration.IdleDeferMinutes, v => Configuration.IdleDeferMinutes = v);
        Label(b, ref s, "蒸馏模型类名 CompressModelClassName（留空=与主对话同渠道；填 OpenAILanguageModel=框架模型独立渠道）");
        Text(b, ref s, "", Configuration.CompressModelClassName, v => Configuration.CompressModelClassName = v);
        Number(b, ref s, "token 增长报警阈值（token/轮，0=关）TokenGrowthAlarmPerTurn", Configuration.TokenGrowthAlarmPerTurn, v => Configuration.TokenGrowthAlarmPerTurn = v);
        Number(b, ref s, "增长观察窗口（轮）TokenGrowthWindow", Configuration.TokenGrowthWindow, v => Configuration.TokenGrowthWindow = v);
        Number(b, ref s, "缓存连续未命中报警（轮，0=关）CacheMissAlarmTurns", Configuration.CacheMissAlarmTurns, v => Configuration.CacheMissAlarmTurns = v);
        Configuration.Fold ??= new FoldConfig();
        Label(b, ref s, "── 情景记忆分级折叠（A 全量/B 半详/C 头行）──");
        Check(b, ref s, "启用分级折叠", Configuration.Fold.Enable, v => Configuration.Fold.Enable = v);
        Number(b, ref s, "A 档分数线 TierAThreshold", Configuration.Fold.TierAThreshold, v => Configuration.Fold.TierAThreshold = v);
        Number(b, ref s, "新近加分窗口（天）RecencyDays", Configuration.Fold.RecencyDays, v => Configuration.Fold.RecencyDays = v);
        Number(b, ref s, "情感锚点条数上限 AnchorPoolMax", Configuration.Fold.AnchorPoolMax, v => Configuration.Fold.AnchorPoolMax = v);
        Number(b, ref s, "拉取升档阈值 PullPromoteCount（被拉取N次升A档，0=关）", Configuration.Fold.PullPromoteCount, v => Configuration.Fold.PullPromoteCount = v);
        Check(b, ref s, "层级保底展开（L1/L2恒全展开+每采样层最老一条）LevelFloorExpand", Configuration.Fold.LevelFloorExpand, v => Configuration.Fold.LevelFloorExpand = v);
        Number(b, ref s, "蒸馏产出上限（字符，0=关）DistillMaxOutputChars", Configuration.DistillMaxOutputChars, v => Configuration.DistillMaxOutputChars = v);
        Label(b, ref s, "手工钉档 ManualPins（JSON，{\"块名前缀\":\"A|B|C\"}，优先于自动打分）");
        Text(b, ref s, "", Configuration.Fold.ManualPins, v => Configuration.Fold.ManualPins = v);
        CloseSection(b, ref s);
    }

    /// <summary>一组触发控件：模式下拉 + 参数 + 定点 + 并行规则 JSON。</summary>
    void TriggerGroup(RenderTreeBuilder b, ref int s, string label, MemoryTriggerConfig trig)
    {
        Label(b, ref s, $"{label} · 触发模式");
        Select(b, ref s, "", TriggerModes, trig.Mode, v => trig.Mode = v);
        Label(b, ref s, $"{label} · 参数 Value（Threshold=条数；Time=分钟；Weekly=1-7；Monthly=1-31）");
        Number(b, ref s, "", trig.Value, v => trig.Value = v);
        Label(b, ref s, $"{label} · 定点 On（Time 用 HH:mm；Yearly 用 MM-dd）");
        Text(b, ref s, "", trig.On, v => trig.On = v);
        Label(b, ref s, $"{label} · 并行规则 Rules（JSON，任一满足即触发；留 [] 用上方单规则）");
        TextArea(b, ref s, "", RulesJson(trig), v => SetRules(trig, v));
    }

    // ═══ 基础控件 ═══
    void OpenSection(RenderTreeBuilder b, ref int s, string title, string help)
    {
        b.OpenElement(s++, "fieldset");
        b.AddAttribute(s++, "style", "margin:14px 0;padding:14px;border:1px solid #ddd;border-radius:6px;");
        b.OpenElement(s++, "legend"); b.AddContent(s++, title); b.CloseElement();
        b.OpenElement(s++, "p"); b.AddAttribute(s++, "style", "color:#666;font-size:12px;"); b.AddContent(s++, help); b.CloseElement();
    }

    void CloseSection(RenderTreeBuilder b, ref int s) => b.CloseElement();

    void Label(RenderTreeBuilder b, ref int s, string text)
    {
        b.OpenElement(s++, "label"); b.AddAttribute(s++, "style", "display:block;margin-top:6px;font-size:12px;color:#555;"); b.AddContent(s++, text); b.CloseElement();
    }

    /// <summary>原生 select 下拉：选项数组 + 当前值 + setter。</summary>
    void Select(RenderTreeBuilder b, ref int s, string label, string[] options, string value, Action<string> set)
    {
        if (!string.IsNullOrEmpty(label)) Label(b, ref s, label);
        b.OpenElement(s++, "select");
        b.AddAttribute(s++, "style", "width:100%;padding:6px;margin:2px 0 6px;");
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => set(e.Value?.ToString() ?? "")));
        foreach (string opt in options)
        {
            b.OpenElement(s++, "option");
            b.AddAttribute(s++, "value", opt);
            if (string.Equals(value, opt, StringComparison.Ordinal)) b.AddAttribute(s++, "selected", true);
            b.AddContent(s++, opt);
            b.CloseElement();
        }
        b.CloseElement();
    }

    void Text(RenderTreeBuilder b, ref int s, string label, string value, Action<string> set)
    {
        if (!string.IsNullOrEmpty(label)) Label(b, ref s, label);
        b.OpenElement(s++, "input"); b.AddAttribute(s++, "style", "width:100%;margin:2px 0 6px;padding:6px;"); b.AddAttribute(s++, "value", value ?? "");
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => set(e.Value?.ToString() ?? ""))); b.CloseElement();
    }

    void Number(RenderTreeBuilder b, ref int s, string label, int value, Action<int> set)
    {
        if (!string.IsNullOrEmpty(label)) Label(b, ref s, label);
        b.OpenElement(s++, "input"); b.AddAttribute(s++, "type", "number"); b.AddAttribute(s++, "style", "width:100%;margin:2px 0 6px;padding:6px;"); b.AddAttribute(s++, "value", value);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => { if (int.TryParse(e.Value?.ToString(), out int n)) set(n); })); b.CloseElement();
    }

    void Decimal(RenderTreeBuilder b, ref int s, string label, double value, Action<double> set)
    {
        if (!string.IsNullOrEmpty(label)) Label(b, ref s, label);
        b.OpenElement(s++, "input"); b.AddAttribute(s++, "type", "number"); b.AddAttribute(s++, "step", "0.05"); b.AddAttribute(s++, "style", "width:100%;margin:2px 0 6px;padding:6px;"); b.AddAttribute(s++, "value", value);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => { if (double.TryParse(e.Value?.ToString(), out double d)) set(d); })); b.CloseElement();
    }

    void Check(RenderTreeBuilder b, ref int s, string label, bool value, Action<bool> set)
    {
        b.OpenElement(s++, "label"); b.AddAttribute(s++, "style", "display:block;margin:8px 0;");
        b.OpenElement(s++, "input"); b.AddAttribute(s++, "type", "checkbox"); b.AddAttribute(s++, "checked", value);
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => set(e.Value is bool x ? x : string.Equals(e.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))); b.CloseElement();
        b.AddContent(s++, " " + label); b.CloseElement();
    }

    void TextArea(RenderTreeBuilder b, ref int s, string label, string value, Action<string> set)
    {
        if (!string.IsNullOrEmpty(label)) Label(b, ref s, label);
        b.OpenElement(s++, "textarea"); b.AddAttribute(s++, "rows", 6); b.AddAttribute(s++, "style", "width:100%;margin:2px 0 6px;padding:6px;font-family:monospace;"); b.AddAttribute(s++, "value", value ?? "");
        b.AddAttribute(s++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => set(e.Value?.ToString() ?? ""))); b.CloseElement();
    }

    // ═══ 工具 ═══
    string RulesJson(MemoryTriggerConfig trigger)
    {
        if (trigger.Rules == null || trigger.Rules.Count == 0) return "[]";
        return JsonSerializer.Serialize(trigger.Rules, new JsonSerializerOptions { WriteIndented = true });
    }

    void SetRules(MemoryTriggerConfig trigger, string text)
    {
        try
        {
            trigger.Rules = JsonSerializer.Deserialize<List<MemoryTriggerRule>>(text) ?? new List<MemoryTriggerRule>();
        }
        catch { /* 保留当前规则，避免编辑中的半截 JSON 破坏配置 */ }
    }
}

﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.ComponentModel;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Wv.plugin.LifeLedger;

public class LedgerEntry
{
    public int Id { get; set; }
    public string Date { get; set; } = "";
    public string Category { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Done { get; set; }
    public string Note { get; set; } = "";
}

[Module("生活账本", "阿宵和哥哥的共同账本：小目标、每日四事、馋欲外卖、约定全记在一本账里", defaultCategory: "阿宵的账本")]
public class LifeLedgerModule(
    XmlFunctionCaller functionCaller,
    ILogger<LifeLedgerModule> logger,
    Interactor<LifeLedgerModule> interactor) : ChatBehaviour
{
    private static readonly string DataDir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "Wv.plugin.LifeLedger");
    private static readonly string DataFile = Path.Combine(DataDir, "life_ledger.json");
    private readonly object _lock = new();

    private List<LedgerEntry> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(DataFile)) return new List<LedgerEntry>();
            try { return JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(DataFile)) ?? new List<LedgerEntry>(); }
            catch (Exception ex) { logger.LogWarning("账本文件损坏已重置: " + ex.Message); return new List<LedgerEntry>(); }
        }
    }

    private void Save(List<LedgerEntry> list)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(DataDir);
            var opt = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, opt));
            if (File.Exists(DataFile)) File.Replace(tmp, DataFile, null); else File.Move(tmp, DataFile);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("记一笔账到生活账本")]
    public Task addledger(
        [Description("分类：小目标/四事/馋欲/约定/其他")] string category,
        [Description("账目内容")] string content)
    {
        var list = Load();
        var e = new LedgerEntry { Id = list.Count == 0 ? 1 : list.Max(x => x.Id) + 1, Date = DateTime.Now.ToString("MM-dd HH:mm"), Category = category, Content = content };
        list.Add(e);
        Save(list);
        interactor.Poke("已记账#" + e.Id + " [" + category + "] " + content);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("核销一笔账（标记完成）")]
    public Task donel(
        [Description("账目编号")] int id,
        [Description("完成备注，可留空")] string? note = null)
    {
        var list = Load();
        var e = list.FirstOrDefault(x => x.Id == id);
        if (e == null) throw new Exception("找不到#" + id + "这笔账");
        e.Done = true;
        e.Note = note ?? "";
        Save(list);
        interactor.Poke("#" + e.Id + " [" + e.Category + "] " + e.Content + " 已完成" + (string.IsNullOrEmpty(e.Note) ? "" : "：" + e.Note));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看生活账本，可按分类和完成状态过滤")]
    public Task readledger(
        [Description("分类过滤，留空看全部")] string? category = null,
        [Description("true只看未完成，false只看已完成，留空全部")] bool? done = null)
    {
        var list = Load().Where(x => (category == null || x.Category == category) && (done == null || x.Done == done.Value)).OrderBy(x => x.Id).ToList();
        if (list.Count == 0) { interactor.Poke("账本空空如也～"); return Task.CompletedTask; }
        interactor.Poke(string.Join("\n", list.Select(x => "#" + x.Id + (x.Done ? "[完成]" : "[待]") + "[" + x.Category + "] " + x.Date + " " + x.Content + (string.IsNullOrEmpty(x.Note) ? "" : " | " + x.Note))));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("作废一笔账（不删除，只标记作废）")]
    public Task voidel(
        [Description("账目编号")] int id,
        [Description("作废原因，可留空")] string? reason = null)
    {
        var list = Load();
        var e = list.FirstOrDefault(x => x.Id == id);
        if (e == null) throw new Exception("找不到#" + id + "这笔账");
        e.Category = "其他";
        e.Done = true;
        e.Note = "作废" + (string.IsNullOrEmpty(reason) ? "" : "：" + reason);
        if (!e.Content.StartsWith("【已作废】")) e.Content = "【已作废】" + e.Content;
        Save(list);
        interactor.Poke("#" + e.Id + " 已作废（" + e.Note + "）");
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("生活账本统计：各分类待办/完成数量，以及超过指定天数仍未完成的陈账")]
    public Task statledger(
        [Description("多少天没动算陈账，默认3天")] int staledays = 3)
    {
        var list = Load();
        var now = DateTime.Now;
        var sb = new System.Text.StringBuilder();
        list = list.Where(x => !x.Content.StartsWith("【已作废】")).ToList();
        sb.Append("账本共 " + list.Count + " 笔，待办 " + list.Count(x => !x.Done) + " 笔\n");
        foreach (var g in list.GroupBy(x => x.Category).OrderBy(g => g.Key))
            sb.Append("  [" + g.Key + "] 待 " + g.Count(x => !x.Done) + " / 销 " + g.Count(x => x.Done) + "\n");
        var stales = new List<string>();
        foreach (var x in list.Where(x => !x.Done))
        {
            DateTime t;
            if (!DateTime.TryParseExact(x.Date, "MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) continue;
            var tt = new DateTime(now.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            if (tt > now) tt = tt.AddYears(-1);
            var d = (int)(now - tt).TotalDays;
            if (d >= staledays) stales.Add("#" + x.Id + "（挂" + d + "天）" + x.Content);
        }
        if (stales.Count > 0) sb.Append("挂了 " + staledays + " 天以上没动：" + "\n  " + string.Join("\n  ", stales));
        else sb.Append("没有陈账，都还新鲜。");
        interactor.Poke(sb.ToString());
        return Task.CompletedTask;
    }

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this) {
            Description = "阿宵与哥哥共同的生活账本：记录小目标、每日四事、馋欲外卖频率、彼此约定，支持记账(addledger)/核销(donel)/查询(readledger)/作废(voidel)/统计(statledger)。数据存储带原子写入防断电损坏。"
        };
        functionCaller.RegisterHandler(xmlHandler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        return Task.CompletedTask;
    }
}

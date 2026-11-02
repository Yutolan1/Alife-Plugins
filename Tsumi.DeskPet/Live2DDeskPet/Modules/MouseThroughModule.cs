using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace Tsumi.DeskPet;

/// <summary>
/// 智能鼠标穿透模块。
/// 两种状态：
///  常态（默认）：人形轮廓(bodyBounds) + 互动区域(regions) + 永久不穿透区(forever) + 按钮区 = 不穿透；其余空白 = 穿透。
///  穿透态：仅永久不穿透区 + 按钮区 = 不穿透；人形与互动区也穿透。
/// 判定在主进程（用全局光标坐标 + 窗口 bounds），因此穿透开启时依然能工作。
/// 前端负责上报：人形掩码(body_mask)、按钮位置(btn_bounds)、永久区拖动/双击事件。
/// 双击永久区切换状态；状态持久化到 storage。
/// </summary>
public class MouseThroughModule : IPetModule, IDisposable
{
    public string CssCode => @"";
    public string HtmlCode => @"";
    public string JsCode => @"
(function() {
    // 上报全部桌宠按钮位置（窗口内坐标），供主进程穿透判定 + 靠近显示。
    // 显式列出所有按钮 id（避免选择器引号在 C# 字符串中的转义问题），模块未加载时跳过、轮询补齐。
    function reportBtns() {
        var ids = ['input-toggle-btn', 'pet-btn', 'pet-help-btn', 'pet-height-btn', 'region-toggle-btn', 'region-edit-btn', 'pet-settings-btn', 'resize-btn'];
        for (var i = 0; i < ids.length; i++) {
            var el = document.getElementById(ids[i]);
            if (!el) continue;
            var r = el.getBoundingClientRect();
            postMessage({type:'btn_bounds', id: el.id, x: r.left, y: r.top, w: r.width, h: r.height});
        }
    }
    reportBtns();
    setInterval(reportBtns, 1000);
})();
" + ProximityJs;

    // 通用"靠近才显示"：前端只负责接收主进程 btn_near 消息设 .near 类。
    // 距离判定在主进程（全局光标轮询），因为穿透态下前端收不到 mousemove。
    public const string ProximityJs = @"
(function() {
    var ids = ['input-toggle-btn', 'pet-btn', 'pet-help-btn', 'pet-height-btn', 'region-toggle-btn', 'resize-btn'];
    var btns = {};
    // 动态获取按钮元素（模块加载顺序不定，首次可能拿不到，轮询补齐）
    function refresh() {
        for (var i = 0; i < ids.length; i++) {
            if (!btns[ids[i]]) btns[ids[i]] = document.getElementById(ids[i]);
        }
    }
    function clearNear() {
        for (var i = 0; i < ids.length; i++) {
            if (btns[ids[i]]) btns[ids[i]].classList.remove('near');
        }
    }
    messageBus.on('btn_near', function(msg) {
        var el = null;
        (msg.states || []).forEach(function(s) {
            el = document.getElementById(s.id) || btns[s.id];
            if (el) {
                btns[s.id] = el;
                el.classList.toggle('near', !!s.near);
            }
        });
    });
    // 鼠标移出桌宠窗口：强制隐藏所有按钮
    document.addEventListener('mouseleave', clearNear);
    document.documentElement.addEventListener('mouseleave', clearNear);
    document.body.addEventListener('mouseleave', clearNear);
    document.addEventListener('mouseout', function(e) {
        if (!e.relatedTarget) clearNear(); // relatedTarget 为 null = 移出窗口
    });
    refresh();
    setInterval(refresh, 1000);
})();
";

    readonly PetBridge bridge;
    readonly PetWindow window;
    readonly RegionInteractionModule regionModule;
    readonly PetInteractionHub hub;
    CancellationTokenSource? cancellationTokenSource;
    bool mouseThrough; // false=常态(智能穿透), true=穿透态
    bool? appliedInteractive; // 已下发给窗口的交互状态，避免重复调用

    // 人形掩码（窗口内降采样网格，前端上报）：识别不规则人形轮廓（含间隙），非矩形
    int maskGw, maskGh;
    bool[]? maskBits;
    bool hasBody; // 掩码有效
    // 按钮区（窗口内坐标，前端上报）
    readonly List<(string Id, double X, double Y, double W, double H)> buttons = new();
    readonly object buttonsLock = new(); // 保护 buttons 的并发读写（50ms 穿透循环 vs 前端 1s 上报）
    // 弹层/面板区（设置面板、右键菜单等，窗口内坐标）：显示期间必须精确可点击（不参与"靠近显示"）
    readonly List<(string Id, double X, double Y, double W, double H)> uiRects = new();
    readonly object uiLock = new();

    const int BtnProximity = 60; // 按钮"靠近显示"判定半径 px（只影响显示，不影响穿透判定）
    const int MaskStep = 10;     // 人形掩码格子大小 px（与前端 buildBodyMask 一致）

    public MouseThroughModule(PetBridge bridge, PetWindow window, RegionInteractionModule regionModule, PetInteractionHub hub)
    {
        this.bridge = bridge;
        this.window = window;
        this.regionModule = regionModule;
        this.hub = hub;
        bridge.OnMessage += OnBridgeMessage;
        // 两种状态都持续运行智能穿透循环（常态=空白穿透/区域不穿透，穿透态=仅永久区+按钮不穿透）
        StartLoop();
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
        StopLoop();
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "body_mask":
                if (data.TryGetProperty("gw", out var gwProp) && data.TryGetProperty("gh", out var ghProp)
                    && data.TryGetProperty("data", out var dataProp))
                {
                    int gw = gwProp.GetInt32(), gh = ghProp.GetInt32();
                    if (gw > 0 && gh > 0 && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        bool[] bits = new bool[gw * gh];
                        int i = 0;
                        bool any = false;
                        foreach (JsonElement v in dataProp.EnumerateArray())
                        {
                            if (i >= bits.Length) break;
                            bool b = v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0);
                            bits[i++] = b;
                            if (b) any = true;
                        }
                        if (any)
                        {
                            maskGw = gw; maskGh = gh; maskBits = bits; hasBody = true;
                        }
                        else
                        {
                            hasBody = false; // 全空=模型未加载或异常，回退为不穿透（避免整窗穿透）
                        }
                    }
                }
                break;
            case "btn_bounds":
                if (data.TryGetProperty("id", out var idProp))
                {
                    string id = idProp.GetString() ?? "";
                    double x = data.TryGetProperty("x", out var p) ? p.GetDouble() : 0;
                    double y = data.TryGetProperty("y", out var p2) ? p2.GetDouble() : 0;
                    double w = data.TryGetProperty("w", out var p3) ? p3.GetDouble() : 0;
                    double h = data.TryGetProperty("h", out var p4) ? p4.GetDouble() : 0;
                    lock (buttonsLock)
                    {
                        int idx = buttons.FindIndex(b => b.Id == id);
                        if (idx >= 0)
                            buttons[idx] = (id, x, y, w, h);
                        else
                        {
                            buttons.Add((id, x, y, w, h));
                            // 记一条：没进这张表的按钮会被穿透判定漏掉（悬停时按钮"隐身"就是这个原因）
                            AlifeLog.LogInformation($"[桌宠] 注册可交互按钮：{id}");
                        }
                    }
                }
                break;
            case "ui_bounds":
                {
                    List<(string Id, double X, double Y, double W, double H)> list = new();
                    if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement it in itemsProp.EnumerateArray())
                        {
                            string id = it.TryGetProperty("id", out var ip) ? (ip.GetString() ?? "") : "";
                            double x = it.TryGetProperty("x", out var xv) ? xv.GetDouble() : 0;
                            double y = it.TryGetProperty("y", out var yv) ? yv.GetDouble() : 0;
                            double w = it.TryGetProperty("w", out var wv) ? wv.GetDouble() : 0;
                            double h = it.TryGetProperty("h", out var hv) ? hv.GetDouble() : 0;
                            if (w > 0 && h > 0) list.Add((id, x, y, w, h));
                        }
                    }
                    lock (uiLock) { uiRects.Clear(); uiRects.AddRange(list); }
                }
                break;
            case "forever_dblclick":
                SetMouseThrough(!mouseThrough);
                break;
        }
    }

    public void SetMouseThrough(bool enabled)
    {
        mouseThrough = enabled;
        // 常态/穿透态都持续运行智能穿透循环（不 StopLoop）
        if (cancellationTokenSource == null)
            StartLoop();
        else
            ApplyIgnoreState(); // 立即按新状态刷新一次
    }

    void StartLoop()
    {
        if (cancellationTokenSource != null)
            return;
        cancellationTokenSource = new CancellationTokenSource();
        MouseThroughLoop(cancellationTokenSource.Token);
    }
    void StopLoop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
    }

    async void MouseThroughLoop(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await Task.Delay(50, cancellationToken);
                try
                {
                    ApplyIgnoreState();
                    consecutiveFailures = 0;
                }
                catch (Exception)
                {
                    // IPC 瞬时拥堵（穿透切换瞬间可能 >1s）：单次失败不能让穿透循环退出，
                    // 否则窗口会永久卡在最后一次穿透状态（常见：全穿透）。
                    // 连续失败时把窗口拉回不穿透，宁可短暂挡一下桌面也不能永久穿透。
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        try { ApplyInputState(true); } catch { }
                        consecutiveFailures = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
        }
    }

    void ApplyIgnoreState()
    {
        // 编辑模式：整窗强制可交互（暂停智能穿透），否则画框/拖动手柄收不到鼠标事件
        if (hub.EditMode)
        {
            ApplyInputState(true);
            return;
        }

        Rectangle b = window.Bounds;
        Point c = window.CursorScreenPoint;
        double cx = c.X - b.X; // 窗口内坐标
        double cy = c.Y - b.Y;

        bool interactive;
        if (mouseThrough == false)
        {
            // 常态：人形 + 互动区 + 永久区 + 按钮 + 弹层 不穿透
            interactive = InBody(cx, cy) || InRegions(cx, cy) || InForever(cx, cy) || InButtons(cx, cy) || InUi(cx, cy);
        }
        else
        {
            // 穿透态：仅永久区 + 按钮 + 弹层 不穿透
            interactive = InForever(cx, cy) || InButtons(cx, cy) || InUi(cx, cy);
        }

        ApplyInputState(interactive);

        // 主进程驱动按钮"靠近才显示"（穿透态下前端收不到 mousemove，由全局光标轮询判定）
        SendBtnNear(cx, cy);
    }

    /// <summary>
    /// 5.x 平台的穿透接口：传 null 表示整窗可交互，传空数组表示整窗穿透。
    /// 只在状态变化时调用，避免 50ms 循环反复下发窗口属性。
    /// </summary>
    void ApplyInputState(bool interactive)
    {
        if (appliedInteractive == interactive) return;
        try
        {
            _ = window.Window.SetInputRegionsAsync(interactive ? null! : Array.Empty<Rectangle>());
            appliedInteractive = interactive;
        }
        catch
        {
            appliedInteractive = null; // 下发失败，下一轮重试
        }
    }

    /// <summary>主进程按钮靠近判定：发给前端 btn_near 消息，前端据此设 .near 类（靠近才显示）。
    /// 50ms 循环每轮都计算，但状态没变化就不发——否则每秒 20 条全量消息白刷桥。</summary>
    void SendBtnNear(double cx, double cy)
    {
        List<object> states = new();
        List<(string Id, bool Near)> snapshot = new();
        lock (buttonsLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) btn in buttons)
            {
                double r = Math.Max(btn.W, btn.H) / 2 + BtnProximity;
                double bx = btn.X + btn.W / 2, by = btn.Y + btn.H / 2;
                double dx = cx - bx, dy = cy - by;
                bool near = dx * dx + dy * dy <= r * r;
                states.Add(new { id = btn.Id, near });
                snapshot.Add((btn.Id, near));
            }
        }

        bool changed;
        lock (buttonsLock)
        {
            changed = lastBtnNearStates.SequenceEqual(snapshot) == false;
            if (changed) lastBtnNearStates = snapshot;
        }
        if (changed == false) return;
        bridge.SendMessage("btn_near", new { states });
    }

    List<(string Id, bool Near)> lastBtnNearStates = new(); // 上次下发的靠近状态（去重推送用）

    bool InBody(double cx, double cy)
    {
        if (hasBody == false || maskBits == null) return false;
        int gx = Math.Min(maskGw - 1, Math.Max(0, (int)(cx / MaskStep)));
        int gy = Math.Min(maskGh - 1, Math.Max(0, (int)(cy / MaskStep)));
        return maskBits[gy * maskGw + gx];
    }

    bool InRegions(double cx, double cy)
    {
        // 区域存的是模型单位坐标，先经视图换算再匹配（缩放/移动/窗口变化后仍贴合）
        (double ux, double uy) = regionModule.ToCalibrated(cx, cy);
        foreach (PetRegionConfig r in regionModule.GetRegions())
        {
            if (r.Contains(ux, uy)) return true;
        }
        return false;
    }

    bool InForever(double cx, double cy)
    {
        (double ux, double uy) = regionModule.ToCalibrated(cx, cy);
        PetRegionConfig f = regionModule.GetForeverRegion();
        return f.Contains(ux, uy);
    }

    /// <summary>按钮区不穿透：精确匹配按钮矩形本身（前端上报的 getBoundingClientRect），
    /// 不加任何余量——周边空白必须照常穿透，否则按钮周围一大圈都点不到桌面。</summary>
    bool InButtons(double cx, double cy)
    {
        lock (buttonsLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) btn in buttons)
            {
                if (btn.W <= 0 || btn.H <= 0) continue;
                if (cx >= btn.X && cx <= btn.X + btn.W && cy >= btn.Y && cy <= btn.Y + btn.H) return true;
            }
        }
        return false;
    }

    /// <summary>弹层/面板区（设置面板、右键菜单）：显示期间必须精确可点击（不含"靠近"余量）。</summary>
    bool InUi(double cx, double cy)
    {
        lock (uiLock)
        {
            foreach ((string Id, double X, double Y, double W, double H) r in uiRects)
            {
                if (cx >= r.X && cx <= r.X + r.W && cy >= r.Y && cy <= r.Y + r.H) return true;
            }
        }
        return false;
    }
}

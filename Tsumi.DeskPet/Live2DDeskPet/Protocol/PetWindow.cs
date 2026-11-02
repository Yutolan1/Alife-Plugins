using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Client;
using Alife.Foundation;
using Alife.Framework;

namespace Tsumi.DeskPet;

/// <summary>按角色隔离的存储键前缀，值为角色的 StorageKey（如 "Character\Mao"）。</summary>
public record PetStorageKey(string Value);

/// <summary>
/// 桌宠窗口封装：负责创建透明置顶窗口、定位/移动/缩放、DPI 与脚本执行，
/// 并通过 StorageSystem 持久化/恢复窗口位置大小（按角色隔离）。
/// 5.x 起客户端用 WebView2 承载窗口（Alife.Client.IAlifeWindow），不再依赖 ElectronNET。
/// 网页资源位于插件 Resources/Live2D/（随插件分发或内嵌到客户端输出目录）。
/// </summary>
public sealed class PetWindow(IAlifeApplication alifeApplication, StorageSystem storage, PetStorageKey storageKey)
    : IAsyncDisposable
{
    public event Action? MouseMoved;

    public IAlifeWindow Window => window ?? throw new InvalidOperationException("桌宠窗口尚未创建");
    public float Dpi => dpi;
    public Rectangle Bounds => bounds;
    public Point Position => position;
    public Point CursorScreenPoint => cursorScreenPoint;

    public void ResetBounds()
    {
        _ = SetBoundsAsync(defaultBounds);
    }

    public async Task SetBoundsAsync(Rectangle newBounds)
    {
        bounds = newBounds;
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        if (window != null)
            await window.SetBoundsAsync(newBounds);
    }

    IAlifeWindow? window;
    float dpi = 1;
    Rectangle bounds;
    Point position;
    Point cursorScreenPoint;
    CancellationTokenSource? cancellationTokenSource;

    readonly string windowBoundsKey = $"{storageKey.Value}/Live2DDeskPet/WindowBounds";
    Rectangle defaultBounds;

    public async Task CreateAsync(string wwwRoot, PetBridge bridge)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("不支持的平台。");

        //获取基础属性（GetScreenSize/GetCursorPosition 返回物理像素，统一换算成 DIP）
        dpi = alifeApplication.GetDpiScale();
        Size screenSize = (alifeApplication.GetScreenSize() / dpi).ToSize();
        defaultBounds = new Rectangle {
            X = Math.Max(0, screenSize.Width - Math.Min(360, screenSize.Width) - 20),
            Y = Math.Max(0, screenSize.Height - Math.Min(540, screenSize.Height) - 20),
            Width = Math.Min(360, screenSize.Width),
            Height = Math.Min(540, screenSize.Height),
        };
        bounds = storage.GetObject(windowBoundsKey, defaultBounds);

        // 位置钳制：只在窗口【完全】移出屏幕时才拉回。
        // 用户可自由把窗口部分拖出屏幕边缘（如桌宠半挂在屏幕左侧，X 为负但右缘仍在屏内），
        // 这类"部分出屏"位置必须精确保存/恢复，不能被误判为"跑出屏幕"。
        const int MinVisible = 60; // 拉回后至少露出 60px
        if (bounds.X + bounds.Width <= 0) bounds.X = 0;                       // 整个窗口在屏幕左侧外
        if (bounds.X >= screenSize.Width) bounds.X = screenSize.Width - MinVisible;   // 整个窗口在屏幕右侧外
        if (bounds.Y + bounds.Height <= 0) bounds.Y = 0;                      // 整个在屏幕上侧外
        if (bounds.Y >= screenSize.Height) bounds.Y = screenSize.Height - MinVisible; // 整个在下侧外
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        cursorScreenPoint = GetCursorPosition();

        //注册 init 监听：必须在窗口创建（开始加载页面）之前完成，避免错过渲染进程在脚本末尾发送的 init 消息
        TaskCompletionSource initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInit(string type, JsonElement _)
        {
            if (type == "init")
            {
                bridge.OnMessage -= OnInit;
                initTcs.TrySetResult();
            }
        }
        bridge.OnMessage += OnInit;
        try
        {
            string url = new Uri(Path.Combine(wwwRoot, "index.html")).AbsoluteUri;
            window = await alifeApplication.CreateWindowAsync(new AlifeWindowOptions {
                Title = "Live2D 桌宠",
                Bounds = bounds,
                TopMost = true,
                Resizable = false,
                Transparent = true,
                Url = url,
            });
            bridge.Attach(window);
            window.BoundsChanged += OnBoundsChanged;

            //唤醒窗口并确保置顶（等价于旧版最顶层 SetAlwaysOnTop）
            await window.Launch();
            await window.SetTopMostAsync(true);

            //等待渲染进程初始化完成，确保 JS 已加载并能通讯后再继续后续消息交互
            using CancellationTokenSource initTimeout = new(TimeSpan.FromSeconds(30));
            await initTcs.Task.WaitAsync(initTimeout.Token);
        }
        finally
        {
            bridge.OnMessage -= OnInit;
        }

        //处理鼠标信息捕获
        cancellationTokenSource = new CancellationTokenSource();
        Loop(cancellationTokenSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        // 空安全：初始化中途失败时 window/cancellationTokenSource 可能还没赋值，
        // 否则 Dispose 自己抛 NRE 会把真正的失败原因盖掉。
        try
        {
            if (cancellationTokenSource != null)
                await cancellationTokenSource.CancelAsync();
            cancellationTokenSource?.Dispose();
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 停止轮询失败: " + e.Message); }

        try
        {
            if (window != null)
            {
                window.BoundsChanged -= OnBoundsChanged;
                await window.DisposeAsync();
            }
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 关闭窗口失败: " + e.Message); }

        try
        {
            boundsSaveTimer?.Dispose();
            boundsSaveTimer = null;
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 停止位置防抖保存失败: " + e.Message); }

        SaveBoundsNow();
    }

    void OnBoundsChanged(Rectangle newBounds)
    {
        bounds = newBounds;
        position = new Point {
            X = bounds.X + bounds.Width / 2,
            Y = bounds.Y + bounds.Height / 2,
        };
        dpi = alifeApplication.GetDpiScale();
        ScheduleBoundsSave();
    }

    // 位置变化后防抖 2 秒自动保存：崩溃/强杀进程也能保住最近位置，
    // 而不是像以前只在正常退出（DisposeAsync）那一刻才写盘。
    const int BoundsSaveDelayMs = 2000;
    Timer? boundsSaveTimer;
    readonly object boundsSaveLock = new();

    void ScheduleBoundsSave()
    {
        boundsSaveTimer?.Dispose();
        try
        {
            boundsSaveTimer = new Timer(_ => SaveBoundsNow(), null, BoundsSaveDelayMs, Timeout.Infinite);
        }
        catch (Exception e) { AlifeLog.LogWarning("PetWindow 启动位置防抖保存失败: " + e.Message); }
    }

    void SaveBoundsNow()
    {
        lock (boundsSaveLock)
        {
            try { storage.SetObject(windowBoundsKey, bounds); }
            catch (Exception e) { AlifeLog.LogWarning("PetWindow 保存位置失败: " + e.Message); }
        }
    }

    Point GetCursorPosition()
    {
        dpi = alifeApplication.GetDpiScale();
        Point newCursorPoint = alifeApplication.GetCursorPosition();
        newCursorPoint.X = (int)(newCursorPoint.X / dpi);
        newCursorPoint.Y = (int)(newCursorPoint.Y / dpi);
        return newCursorPoint;
    }

    async void Loop(CancellationToken cancellationToken = default)
    {
        // 单次取坐标失败不终止循环：高频消息下平台层可能短暂拥堵，
        // 一次失败就退出会让光标位置永久停更 → 穿透/靠近判定用旧坐标 → 窗口卡死。
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(30, cancellationToken);

                Point newCursorPoint = GetCursorPosition();
                if (cursorScreenPoint.X != newCursorPoint.X || cursorScreenPoint.Y != newCursorPoint.Y)
                {
                    cursorScreenPoint = newCursorPoint;
                    MouseMoved?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // 瞬时失败：等 500ms 重试，不退出循环
                AlifeLog.LogWarning("PetWindow 轮询瞬时失败，500ms 后重试: " + e.Message);
                try { await Task.Delay(500, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}

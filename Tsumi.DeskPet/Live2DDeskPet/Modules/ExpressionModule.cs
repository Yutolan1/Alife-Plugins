using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace Tsumi.DeskPet;

public class ExpressionModule : IPetModule, IDisposable
{
    public string JsCode => @"
messageBus.on('expression', (msg) => model.expression(msg.id));
messageBus.on('motion', (msg) => model.motion(msg.group, msg.index, PIXI.live2d.MotionPriority.FORCE));
";

    /// <summary>没指定秒数时，表情保持多久后自动收回默认（第一个）表情。想改就改这里。</summary>
    const double DefaultExpressionSeconds = 5;

    /// <summary>
    /// 播放表情。seconds 由她决定：
    /// 不填=默认 <see cref="DefaultExpressionSeconds"/> 秒后收回；&gt;0=保持这么多秒后收回；&lt;=0=一直保持不收回。
    /// </summary>
    public void PlayExpression(string? id, double? seconds = null)
    {
        bridge.SendMessage("expression", new { id });

        if (seconds is double s && s <= 0)
        {
            heldExpression = id;            // 一直保持
            lastPlayExpressionTime = null;
            return;
        }

        heldExpression = null;
        expressionHoldSeconds = seconds ?? DefaultExpressionSeconds;
        lastPlayExpressionTime = DateTime.Now;
    }

    /// <summary>
    /// 播放动作。seconds 由她决定：
    /// 不填=自然播放一次（循环动作本来就会一直播）；&gt;0=保持这么多秒（一次性动作按自身时长重播）；
    /// &lt;=0=一直保持（一次性动作按时长一直重播）。注意：自循环动作无法中途停，会一直播下去。
    /// </summary>
    public void PlayMotion(string group, int index, double? seconds = null)
    {
        StopMotionHold();
        bridge.SendMessage("motion", new { group, index });
        if (seconds is null) return;
        if (metadata.Motions.Count == 0) return;

        string? name = metadata.Motions.FirstOrDefault(kv => kv.Value.Group == group && kv.Value.Index == index).Key;
        if (string.IsNullOrEmpty(name)) return;
        if (metadata.MotionMeta.TryGetValue(name, out (bool Loop, double Duration) meta) == false) return;

        double keep = seconds.Value;
        if (meta.Loop && keep <= 0) return;          // 自循环 + 一直保持 → 天然满足
        double period = meta.Duration > 1 ? meta.Duration - 0.2 : 4;

        motionHoldCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource!.Token);
        _ = MotionHoldLoop(group, index, period, keep, motionHoldCts.Token);
    }

    readonly PetBridge bridge;
    readonly PetModelMetadata metadata;
    CancellationTokenSource? cancellationTokenSource;
    CancellationTokenSource? motionHoldCts;
    DateTime? lastPlayExpressionTime;
    double expressionHoldSeconds = DefaultExpressionSeconds;
    string? heldExpression;

    public ExpressionModule(PetBridge bridge, PetModelMetadata metadata)
    {
        this.bridge = bridge;
        this.metadata = metadata;
        cancellationTokenSource = new CancellationTokenSource();

        if (metadata.Expressions.Count != 0)
            AutoRevertLoop(cancellationTokenSource.Token);
    }
    public void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        motionHoldCts?.Cancel();
        motionHoldCts?.Dispose();
    }

    void StopMotionHold()
    {
        try { motionHoldCts?.Cancel(); motionHoldCts?.Dispose(); } catch { }
        motionHoldCts = null;
    }

    /// <summary>保持动作：按动作自身时长反复重发，直到到点或被她换掉。</summary>
    async Task MotionHoldLoop(string group, int index, double periodSeconds, double keepSeconds, CancellationToken token)
    {
        try
        {
            double elapsed = 0;
            while (token.IsCancellationRequested == false)
            {
                await Task.Delay(TimeSpan.FromSeconds(periodSeconds), token);
                elapsed += periodSeconds;
                if (keepSeconds > 0 && elapsed >= keepSeconds) return;   // 到点就不再重发
                bridge.SendMessage("motion", new { group, index });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AlifeLog.LogError(e); }
    }

    async void AutoRevertLoop(CancellationToken cancellationToken)
    {
        while (cancellationToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(500, cancellationToken);
                if (heldExpression != null) continue;      // 一直保持的表情：不收回
                if (lastPlayExpressionTime == null) continue;

                if (DateTime.Now - lastPlayExpressionTime.Value > TimeSpan.FromSeconds(expressionHoldSeconds))
                {
                    if (metadata.Expressions.Count > 0)
                        bridge.SendMessage("expression", new { id = metadata.Expressions.First() });
                    lastPlayExpressionTime = null;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                // 单次异常不能让循环退出（之前空列表 First() 抛异常，循环直接死掉，表情就再也不恢复了）
                AlifeLog.LogError(e);
                await Task.Delay(1000);
            }
        }
    }
}

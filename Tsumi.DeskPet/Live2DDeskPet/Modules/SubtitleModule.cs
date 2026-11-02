using System;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace Tsumi.DeskPet;

public class SubtitleModule : IPetModule, IDisposable
{
    public string CssCode => @"
#bubble-container {
    position:fixed; top:-12px;
    left:var(--model-cx, 50%); /* 模型中心（pet.js 实时更新），水平方向始终贴住模型 */
    transform:translateX(-50%) scale(var(--ui-scale));
    transform-origin:top center; width:250px;
    pointer-events:none; z-index:1000;
    opacity:0; transition:opacity 0.3s ease;
}
#bubble-container.show { opacity:1; }
#bubble {
    background:rgba(255,255,255,0.95);
    backdrop-filter:blur(8px); border-radius:18px;
    padding:12px 16px; font-family:'Microsoft YaHei',sans-serif;
    font-size:14px; color:#444;
    box-shadow:0 4px 15px rgba(0,0,0,0.1);
    border:1px solid rgba(255,255,255,0.5);
    position:relative; line-height:1.5;
    /* 借鉴原始 BubbleModule 的展示机制（无滚动条、无翻页）：
       最多显示两行，超出部分以省略号截断。
       max-height 精确锁 2 行（1.5×14px×2=42px）：line-clamp 裁切第三行时
       可能露出半行文字，高度上限保证绝不多显一行 */
    display:-webkit-box;
    -webkit-line-clamp:2;
    -webkit-box-orient:vertical;
    overflow:hidden;
    max-height:42px;
    word-break:break-all;
}
#bubble::after {
    content:''; position:absolute; bottom:-8px; left:50%;
    transform:translateX(-50%);
    border-left:8px solid transparent;
    border-right:8px solid transparent;
    border-top:8px solid rgba(255,255,255,0.95);
}
";

    public string HtmlCode => "<div id='bubble-container'><div id='bubble'></div></div>";

    public string JsCode => @"
messageBus.on('bubble', (msg) => {
    document.getElementById('bubble').innerText = msg.text;
    document.getElementById('bubble-container').classList.add('show');
});
messageBus.on('hide-bubble', () => {
    document.getElementById('bubble-container').classList.remove('show');
});
";

    public void Show(string text)
    {
        bridge.SendMessage("bubble", new { text });
        lastShowingTime = DateTime.Now;
    }

    public void Hide() => bridge.SendMessage("hide-bubble");

    readonly PetBridge bridge;
    readonly CancellationTokenSource? cancellationTokenSource;
    DateTime? lastShowingTime;

    public SubtitleModule(PetBridge bridge)
    {
        this.bridge = bridge;

        cancellationTokenSource = new CancellationTokenSource();
        AutoHideLoop(cancellationTokenSource.Token);
    }
    public void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
    }


    async void AutoHideLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                await Task.Delay(1000, cancellationToken);
                if (lastShowingTime == null)
                    continue;

                if (DateTime.Now - lastShowingTime > TimeSpan.FromSeconds(6))
                {
                    Hide();
                    lastShowingTime = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AlifeLog.LogError(e);
        }
    }
}
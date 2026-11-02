using System;
using System.Drawing;
using System.Text.Json;

namespace Tsumi.DeskPet;

public class DragModule : IPetModule, IDisposable
{
    public string JsCode => @"
let isDragging = false;
window.addEventListener('mousedown', function(e) {
    if (e.button !== 0) return;
    if (!e.target.closest('[data-drag]')) return;
    isDragging = true;
    postMessage({type:'drag_start'});
});
window.addEventListener('mousemove', function(e) {
    if (isDragging === true) {
        postMessage({type:'drag_move',dx:e.movementX,dy:e.movementY});
    }
});
window.addEventListener('mouseup', function(e) {
    isDragging = false;
});
";

    readonly PetBridge bridge;
    readonly PetWindow window;
    Rectangle startPosition;
    bool hasStartPosition;

    public DragModule(PetBridge bridge, PetWindow window)
    {
        this.bridge = bridge;
        this.window = window;
        bridge.OnMessage += OnBridgeMessage;
    }
    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "drag_start":
                startPosition = window.Bounds;
                hasStartPosition = true;
                break;
            case "drag_move":
                if (hasStartPosition)
                {
                    startPosition.X += data.GetProperty("dx").GetInt32();
                    startPosition.Y += data.GetProperty("dy").GetInt32();
                    _ = window.SetBoundsAsync(startPosition);
                }
                break;
            case "pet_window_drag":
                // 永久区拖动：带动整个桌宠窗口移动
                Rectangle b = window.Bounds;
                b.X += data.GetProperty("dx").GetInt32();
                b.Y += data.GetProperty("dy").GetInt32();
                _ = window.SetBoundsAsync(b);
                break;
        }
    }
}
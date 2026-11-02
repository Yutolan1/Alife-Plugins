using System;
using System.Text.Json;

namespace Tsumi.DeskPet;

/// <summary>
/// 已停用（保留空壳避免破坏 DI 装配）：点击交互统一由 <see cref="RegionInteractionModule"/>
/// 处理（窗口坐标区域匹配 + 连点分档触发）。本模块不再注册任何前端事件。
/// </summary>
public class PokeInteractionModule : IPetModule, IDisposable
{
    public string JsCode => @"
// 已停用：点击交互统一由 RegionInteractionModule 处理（坐标区域+连点分档）。
";

    readonly PetBridge bridge;

    public PokeInteractionModule(PetBridge bridge)
    {
        this.bridge = bridge;
    }
    public void Dispose() { }
}

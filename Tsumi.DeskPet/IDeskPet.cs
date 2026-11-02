using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Tsumi.DeskPet;

public interface IDeskPet
{
    public event Action<string>? OnInput;
    public event Action<string>? OnInteracted;
    /// <summary>模型切换完成事件，参数为新模型名（供上层播报与刷新常驻信息）。</summary>
    public event Action<string>? ModelSwitched { add { } remove { } }

    public string[] SupportedExpressions { get; }
    public string[] SupportedMotions { get; }

    /// <summary>当前使用的模型名。</summary>
    public string CurrentModel => "";
    /// <summary>本机可用的模型名列表。</summary>
    public string[] AvailableModels => Array.Empty<string>();
    /// <summary>切换到指定模型；成功返回 true。</summary>
    public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(false);

    public Task ShowUsing(bool isUsing);
    public Task ShowSubtitle(string? subtitle);
    /// <summary>播放表情；seconds 由 AI 决定：不填=默认秒数后收回，&gt;0=保持这么多秒，&lt;=0=一直保持。</summary>
    public Task ShowExpression(string? expression, double? seconds = null);
    /// <summary>播放动作；seconds 同上（不填=自然播放一次）。</summary>
    public Task ShowMotion(string? motion, double? seconds = null);
    public Task<Vector2> GetPosition();
    public Task Move(Vector2 offset, float seconds);
}
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Alife.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Tsumi.DeskPet;

/// <summary>
/// 桌宠主进程 ⇄ 渲染进程的桥。
/// 5.x 起由 <see cref="IAlifeWindow"/> 承载消息：窗口收到的消息统一进入 <see cref="OnMessage"/>，
/// 发往页面的消息走 <see cref="IAlifeWindow.PostMessageAsync"/>，信封对象为 { type: string, ...payload }。
/// </summary>
public sealed class PetBridge : IDisposable
{
    public event Action<string, JsonElement>? OnMessage;

    /// <summary>向渲染进程发送消息（合并 type 与 payload 为单一信封对象）。</summary>
    public void SendMessage(string type, object? payload = null)
    {
        try
        {
            JObject envelope = new() { ["type"] = type };
            if (payload != null)
            {
                JObject payloadObj = JObject.FromObject(payload);
                foreach (KeyValuePair<string, JToken?> kvp in payloadObj)
                    envelope[kvp.Key] = kvp.Value;
            }
            _ = window.Window.PostMessageAsync(envelope.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "发送桌宠消息失败");
        }
    }

    /// <summary>在渲染进程执行 JS 并返回结果。</summary>
    public async Task<string?> ExecuteJavaScriptAsync(string script)
    {
        try
        {
            return await window.Window.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "执行桌宠脚本失败");
            return null;
        }
    }

    readonly PetWindow window;
    readonly ILogger<Live2DDeskPet> logger;
    IAlifeWindow? attached;

    public PetBridge(PetWindow window, ILogger<Live2DDeskPet> logger)
    {
        this.window = window;
        this.logger = logger;
    }

    /// <summary>窗口创建完成后接上消息通道（PetWindow.CreateAsync 调用一次）。</summary>
    internal void Attach(IAlifeWindow alifeWindow)
    {
        attached = alifeWindow;
        attached.MessageReceived += OnIpcMessage;
    }

    public void Dispose()
    {
        if (attached != null)
        {
            attached.MessageReceived -= OnIpcMessage;
            attached = null;
        }
    }

    void OnIpcMessage(string json)
    {
        // logger.LogInformation(payload?.ToString());

        if (string.IsNullOrEmpty(json))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("type", out JsonElement typeProp) == false)
                return;

            string? type = typeProp.GetString();
            if (type == null)
                return;

            try
            {
                OnMessage?.Invoke(type, root);
            }
            catch (Exception e)
            {
                logger.LogError(e, "执行桌宠前端消息回调失败");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析桌宠前端消息失败");
        }
    }
}

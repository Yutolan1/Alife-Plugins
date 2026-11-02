using System;
using System.IO;
using System.Threading;

namespace Tsumi.DeskPet;

/// <summary>
/// 多进程安全的文件写入：先写临时文件再 Move 覆盖（原子替换），读者永远不会读到半截内容；
/// 目标恰好被其他实例打开时短暂重试，彻底失败再退回直接覆盖（内容写进去优先于原子性）。
/// 背景：桌宠的 merged 模型 / 区域 / 配置 json 可能被多个角色实例或管理界面同时碰，
/// 裸 File.WriteAllText 并发时会互相打断（IOException: 文件正由另一进程使用）或写出半截内容。
/// </summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string contents)
    {
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, contents);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, true);
                    return;
                }
                catch (IOException) when (attempt < 4) { Thread.Sleep(60); }
                catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(60); }
            }
        }
        catch (Exception)
        {
            File.WriteAllText(path, contents); // 兜底：直接覆盖，至少内容落盘
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

namespace StudioX.Application.StcDebugging;

using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class Mon51DebugSession
{
    private bool userProgramVerified = true;
    private string? downloadRecoveryPath;
    internal bool UserDownloadRequested
    {
        get; init;
    }

    internal async Task DownloadUserProgramAsync(StcDebugArtifact bundle, CancellationToken token)
    {
        var bytes = Mon51DownloadPreparation.Binary(bundle);
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (points.Count != 0 || HasSymbols)
            {
                throw new StudioXException("MON51_DOWNLOAD_STATE", "下载须在新连接中执行，不能覆盖已布置断点或加载源码的会话。");
            }
            // 从擦除前开始阻止失败清理续跑；只有完整回读及复位跳板核对通过才能恢复正常结束行为。
            userProgramVerified = false;
            await JsonStore.WriteAsync(downloadRecoveryPath!, new
            {
                project = bundle.ProjectDirectory,
                imageSha256 = bundle.ImageSha256,
                symbolsSha256 = bundle.SymbolsSha256,
                sourceStamp = bundle.SourceStamp,
                bytes = bytes.Length,
                startedUtc = DateTimeOffset.UtcNow,
                message = "用户程序写入未确认完成；禁止普通附加后续跑，须明确重新下载并完整核对"
            }, token);
            Output?.Invoke($"Mon51 用户程序下载：{bytes.Length} 字节 · BIN SHA-256 {Convert.ToHexString(SHA256.HashData(bytes))} · 不写芯片配置");
            await client!.EraseUserProgramAsync(token);
            for (var address = 3; address < bytes.Length; address += 40)
            {
                await client.WriteUserProgramBlockAsync((ushort)address, bytes.AsSpan(address, Math.Min(40, bytes.Length - address)).ToArray(), token);
            }
            // 原厂将三字节复位向量单独提交，监控同时维护 DBFD 跳板，不能混入首个数据块。
            await client.WriteUserProgramBlockAsync(0, bytes[..3], token);
            for (var address = 0; address < bytes.Length; address += 128)
            {
                var count = Math.Min(128, bytes.Length - address);
                if (!(await client.ReadAsync(Mon51MemorySpace.Code, (ushort)address, count, token)).SequenceEqual(bytes.AsSpan(address, count).ToArray()))
                {
                    throw new StudioXException("MON51_VERIFY", $"下载后用户 CODE 0x{address:X4} 回读不一致；目标保持暂停。");
                }
            }
            if (!(await client.ReadAsync(Mon51MemorySpace.Code, 0xdbfd, 3, token)).SequenceEqual(bytes[..3]))
            {
                throw new StudioXException("MON51_VERIFY", "复位跳板回读不一致；目标保持暂停。");
            }
            await client.SetPcAsync(0, token);
            await ReadSnapshotAsync(token);
            if (Pc != 0xdbfd)
            {
                throw new StudioXException("MON51_VERIFY", "下载后未到达 Mon51 逻辑复位入口；目标保持暂停。");
            }
            File.Delete(downloadRecoveryPath!);
            userProgramVerified = true;
            SetState(DebugState.Stopped, $"已下载并完整核对 {bytes.Length} 字节用户程序 · 逻辑复位入口 0xDBFD · 正在核对源码符号");
            Output?.Invoke("Mon51 用户程序下载成功：完整代码和复位跳板回读一致；未重新上电，尚未运行。");
        }
        catch (Exception error)
        {
            SetState(DebugState.Faulted, "Mon51 用户程序下载未完成，目标保持暂停：" + error.Message);
            Output?.Invoke(error.ToString());
            throw;
        }
        finally { gate.Release(); }
    }
}

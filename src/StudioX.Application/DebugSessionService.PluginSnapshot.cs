namespace StudioX.Application;

using System.Text.Json;
using StudioX.Engine.Debugging;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    /// <summary>只取已有快照，不发 MI 命令；工程检查与状态读取共用调试命令锁。</summary>
    public async Task<PluginDebugSnapshotRequest> CapturePluginSnapshotAsync(string project, long revision, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var matches = string.Equals(ProjectDirectory, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase);
            var state = matches ? State : DebugState.Disconnected;
            // 运行、结束或切换工程后不给扩展传递上次暂停数据，避免被解释成当前读数。
            return new(state.ToString(), matches && IsActive && IsHardware,
                JsonSerializer.SerializeToElement(matches && state == DebugState.Stopped ? Snapshot : DebugSnapshot.Empty, JsonStore.Options))
            {
                Revision = revision,
                Reason = matches ? Reason : "当前调试会话属于其他工程"
            };
        }
        finally { gate.Release(); }
    }
}

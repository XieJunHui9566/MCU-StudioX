namespace StudioX.Cli;

using System.Runtime.InteropServices;
using StudioX.Application.Mcp;

/// <summary>外部目录只读会话授权与每次变更、设备动作都由本机用户在独立系统对话框中确认。</summary>
internal sealed class ExternalMcpAuthorizer : IStudioXMcpAuthorizer
{
    private const uint YesNoWarningDefaultNoForeground = 0x00000004 | 0x00000030 |
        0x00000100 | 0x00010000 | 0x00040000;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 没有可交互桌面时默认拒绝，避免无界面的 MCP 客户端隐式读取外部目录或修改工程与设备。
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return false;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var description = $"外部 MCP 客户端请求执行操作：\n\n" +
                    $"工程：{request.Project}\n" +
                    $"工具：{request.Tool}\n" +
                    $"权限：{request.Permission}\n" +
                    $"动作：{request.Summary}\n\n" +
                    (request.Permission == StudioXMcpPermission.ExternalRead
                        ? "是否允许本 MCP 会话只读访问所列外部目录？会话结束后授权失效。"
                        : "是否仅批准这一次调用？");
                try
                {
                    return MessageBoxW(nint.Zero, description, "MCU StudioX MCP 授权",
                        YesNoWarningDefaultNoForeground) == 6;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}

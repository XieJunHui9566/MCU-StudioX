namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;

/// <summary>触发已授权的工程构建，并分页保留原始编译日志。</summary>
internal sealed class BuildMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "project_build")]
    [Description("按当前宿主授权策略用工程锁定的内置开发环境组件执行配置或编译；不会下载或连接硬件。返回有界诊断和完整日志路径。")]
    public async Task<string> ProjectBuildAsync(
        [Description("configure 只配置 CMake；build 编译固件。")]
        string action = "build", CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (action is not ("build" or "configure"))
        {
            throw new StudioXException("MCP_BUILD_ACTION", "构建动作只能是 build 或 configure。");
        }
        if (Context.AgentEditor is { } editor)
        {
            await editor.SaveForBuildAsync(cancellationToken).ConfigureAwait(false);
        }
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("project_build", action == "build"
                ? "使用当前工程锁定的内置开发环境组件编译固件。"
                : "使用当前工程锁定的内置开发环境组件配置 CMake。",
            StudioXMcpPermission.Build, cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var snapshot = Context.AgentEditor is { } beforeEditor ? await beforeEditor.SnapshotAsync(cancellationToken) : null;
        using var operation = Context.Operations.Start("project_build", TimeSpan.FromMinutes(20), cancellationToken,
            PathBoundary.Resolve(Project, ".build/studiox-build.log"));
        try
        {
            var report = action == "build"
                ? await Services.Builds.BuildAsync(Project, operation.Progress, operation.Token, operation.Output).ConfigureAwait(false)
                : await Services.Builds.ConfigureAsync(Project, operation.Progress, operation.Token, operation.Output).ConfigureAwait(false);
            var stale = false;
            if (Context.AgentEditor is { } completedEditor && snapshot is not null)
            {
                var after = await completedEditor.SnapshotAsync(cancellationToken);
                stale = after.Documents.Any(d => d.Text != d.Source.Text) || snapshot.Documents.Any(d =>
                    after.Documents.FirstOrDefault(a => a.Source.RelativePath.Equals(d.Source.RelativePath, StringComparison.OrdinalIgnoreCase)) is { } current && current.Text != d.Text);
                if (action == "build")
                {
                    completedEditor.RecordValidation(report.Success, stale);
                }
            }
            operation.Complete(report.Success, report.ExitCode, report.LogPath, report.TimedOut);
            return JsonSerializer.Serialize(new
            {
                action,
                report.Success,
                report.ExitCode,
                report.TimedOut,
                stale,
                evidence = action == "build" ? "内置开发环境组件编译；未连接或验证硬件" : "CMake 配置；尚未编译或验证硬件",
                artifacts = report.Artifacts.Take(12),
                report.LogPath,
                log = LimitOutput(report.Log, 12_000)
            });
        }
        catch (OperationCanceledException error) when (operation.Token.IsCancellationRequested)
        {
            if (action == "build")
            {
                Context.AgentEditor?.RecordValidation(false, false);
            }
            var interruption = operation.Interrupted(error);
            if (ReferenceEquals(interruption, error))
            {
                throw;
            }
            throw interruption;
        }
        catch
        {
            if (action == "build")
            {
                Context.AgentEditor?.RecordValidation(false, false);
            }
            throw;
        }
    }

    [McpServerTool(Name = "project_operation_status")]
    [Description("查询本 MCP 会话当前或最近的编译/ESP 下载阶段、耗时、退出状态及有界原始输出；不连接设备。运行期间可并发查询，状态时间来自主机 UTC。")]
    public async Task<string> ProjectOperationStatusAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return Context.Operations.Status();
    }

    [McpServerTool(Name = "project_operation_cancel")]
    [Description("按操作标识请求取消本 MCP 会话的编译或 ESP 下载；不影响其他会话。返回 stopping 表示仍在清理，需查询终态；下载取消不代表 Flash 已恢复，不自动重试。")]
    public async Task<string> ProjectOperationCancelAsync(
        [Description("project_operation_status 返回的准确 operationId。")]
        string operationId, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var tool = Context.Operations.ToolFor(operationId);
        await RequireApprovalAsync("project_operation_cancel", "请求停止本会话当前操作并等待工具进程退出。",
            tool == "firmware_download" ? StudioXMcpPermission.FirmwareDownload : StudioXMcpPermission.Build,
            cancellationToken).ConfigureAwait(false);
        return Context.Operations.Cancel(operationId);
    }

    [McpServerTool(Name = "project_build_log")]
    [Description("按字节偏移读取当前工程最近一次 StudioX 编译日志的片段；路径固定为 .build/studiox-build.log，不接受任意文件路径。")]
    public async Task<string> ProjectBuildLogAsync(
        [Description("从日志开头起的字节偏移，默认 0。")]
        long offsetBytes = 0,
        [Description("本次读取字节数，范围 1–16000，默认 12000。")]
        int maxBytes = 12000,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (offsetBytes < 0 || maxBytes is < 1 or > 16000)
        {
            throw new StudioXException("MCP_BUILD_LOG_RANGE", "日志偏移或单次读取长度无效。");
        }
        var path = PathBoundary.Resolve(Project, ".build/studiox-build.log");
        if (!File.Exists(path))
        {
            throw new StudioXException("MCP_BUILD_LOG_MISSING", "当前工程没有 StudioX 编译日志，请先编译。");
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offsetBytes > stream.Length)
        {
            throw new StudioXException("MCP_BUILD_LOG_RANGE", "日志偏移超过文件长度。");
        }
        stream.Seek(offsetBytes, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(maxBytes, stream.Length - offsetBytes)];
        var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            offsetBytes,
            nextOffsetBytes = offsetBytes + count,
            totalBytes = stream.Length,
            endOfFile = offsetBytes + count >= stream.Length,
            text = Encoding.UTF8.GetString(buffer, 0, count)
        });
    }


}

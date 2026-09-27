namespace StudioX.Application.Mcp;

using StudioX.Foundation;

/// <summary>调试与下载共用的工程占用检查，防止新工具组越过 IDE 的活动会话。</summary>
internal sealed class McpDebugAccess(DebugSessionService debug, string project)
{
    internal bool ProjectMatches() => debug.ProjectDirectory is null ||
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(debug.ProjectDirectory))
            .Equals(project, StringComparison.OrdinalIgnoreCase);

    internal void EnsureCanStart()
    {
        if (!ProjectMatches())
        {
            throw new StudioXException("DEBUG_PROJECT", "IDE 调试器已绑定其他工程，不能由 MCP 切换或停止该会话。");
        }
        if (debug.IsActive)
        {
            throw new StudioXException("DEBUG_STATE", "已有活动调试会话；请先在原会话中结束，不会由 MCP 自动停止。");
        }
    }
}

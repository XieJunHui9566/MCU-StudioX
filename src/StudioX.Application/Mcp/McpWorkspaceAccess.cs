namespace StudioX.Application.Mcp;

using StudioX.Engine;
using StudioX.Foundation;
using static McpWorkspacePathPolicy;

/// <summary>集中校验绑定工程及源码访问范围，工具组不能各自放宽文件权限。</summary>
internal sealed class McpWorkspaceAccess(string project)
{
    private string Project { get; } = project;
    internal async Task<ProjectManifest> RequireWorkspaceProjectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(Project) || (File.GetAttributes(Project) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("MCP_PROJECT", "绑定工程不存在或是链接目录。");
        }
        return await ProjectService.ReadAsync(Project, token).ConfigureAwait(false);
    }

    internal string RequireWorkspaceSourceFile(string relative)
    {
        if (!IsWorkspaceSourceFile(relative))
        {
            throw new StudioXException("MCP_PATH", "只能访问当前工程的安全源码文本文件。");
        }
        return PathBoundary.Resolve(Project, relative);
    }

    internal string RequireWorkspaceWritableSourceFile(string relative)
    {
        if (!IsWorkspaceSourceFile(relative))
        {
            throw new StudioXException("MCP_PATH", "只能修改当前工程内安全的相对源码文件。");
        }
        if (!IsWorkspaceWritableSourceFile(relative))
        {
            throw new StudioXException("MCP_FILE_READ_ONLY", "器件配置或受保护路径只能读取，不能由 Agent 修改。");
        }
        return RequireWorkspaceSourceFile(relative);
    }

    internal void RequireWorkspaceSourceDirectory(string relative)
    {
        if (!IsWorkspaceSourceDirectory(relative))
        {
            throw new StudioXException("MCP_PATH", "只能访问当前工程的安全源码目录。");
        }
        _ = PathBoundary.Resolve(Project, relative);
    }

    internal void RequireWorkspaceWritableSourceDirectory(string relative)
    {
        if (!IsWorkspaceSourceDirectory(relative) || IsUnderDeviceDirectory(relative) ||
            !ProjectFileService.CanCreateIn(relative))
        {
            throw new StudioXException("MCP_DIRECTORY", "不能在器件配置或受保护目录新建源码目录。");
        }
        _ = PathBoundary.Resolve(Project, relative);
    }


}

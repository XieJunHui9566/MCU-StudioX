namespace StudioX.Application.Mcp;

using System.Collections.Concurrent;
using System.Text.Json;
using StudioX.Foundation;
using static ExternalProjectPathPolicy;

/// <summary>拥有本次会话的外部只读授权；所有工具组共用目录标识，结束会话即失效。</summary>
internal sealed class ExternalProjectAccess(McpSessionContext context)
{
    private string Project => context.Project;
    private readonly ConcurrentDictionary<string, string> externalRoots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim externalOpenGate = new(1, 1);
    /// <summary>审批前后都校验目录，防止用户确认期间路径被替换。</summary>
    internal async Task<string> OpenAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        await context.Workspace.RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var root = await Task.Run(() => ValidateExternalRoot(directory), cancellationToken).ConfigureAwait(false);
        if (root.Equals(Project, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("MCP_EXTERNAL_ROOT", "当前工程请使用 project_* 工具。");
        }
        await externalOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 同一会话中再次打开同一目录时复用已授权标识，避免重复询问和耗尽目录槽位。
            var existing = externalRoots.FirstOrDefault(item =>
                item.Value.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (existing.Key is not null)
            {
                return JsonSerializer.Serialize(new
                {
                    rootId = existing.Key,
                    directory = root,
                    readOnly = true,
                    alreadyApproved = true
                });
            }
            if (externalRoots.Count >= 8)
            {
                throw new StudioXException("MCP_EXTERNAL_ROOTS", "本次会话最多同时读取 8 个外部目录。");
            }
            await context.RequireApprovalAsync("external_project_open",
                $"允许本次工程 MCP 会话只读浏览、搜索和读取外部目录：{root}。仅此目录内的安全文件可见；复制到当前工程仍会逐次请求写入授权。",
                StudioXMcpPermission.ExternalRead, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = await Task.Run(() => ValidateExternalRoot(root), cancellationToken).ConfigureAwait(false);
            var rootId = Guid.NewGuid().ToString("N");
            externalRoots[rootId] = root;
            return JsonSerializer.Serialize(new
            {
                rootId,
                directory = root,
                readOnly = true,
                alreadyApproved = false
            });
        }
        finally { externalOpenGate.Release(); }
    }

    internal async Task<string> ListRootsAsync(CancellationToken cancellationToken = default)
    {
        await context.Workspace.RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            roots = externalRoots.OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { rootId = item.Key, directory = item.Value, readOnly = true }).ToArray()
        });
    }

    internal async Task<string> RequireExternalRootAsync(string rootId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(rootId) || !externalRoots.TryGetValue(rootId, out var root))
        {
            throw new StudioXException("MCP_EXTERNAL_GRANT", "外部目录未获得本次 MCP 会话授权，请先调用 external_project_open。");
        }
        return await Task.Run(() => ValidateExternalRoot(root), token).ConfigureAwait(false);
    }


    internal bool ContainsGrantedPath(string path) => externalRoots.Values.Any(root =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));

    internal void ClearGrants() => externalRoots.Clear();

}

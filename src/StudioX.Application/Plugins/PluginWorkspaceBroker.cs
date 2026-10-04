namespace StudioX.Application.Plugins;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application.Mcp;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>插件复用应用 MCP 服务；每个插件保持独立授权与设备观察会话。</summary>
public sealed class PluginWorkspaceBroker : IAsyncDisposable
{
    private readonly WorkbenchService services;
    private readonly string project;
    private readonly IStudioXMcpAuthorizer authorizer;
    private readonly Func<Task<bool>>? hasUnsavedDocuments;
    private readonly IPluginEditorAccess? editor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, StudioXMcpSession> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> stopped = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public PluginWorkspaceBroker(WorkbenchService services, string project, IStudioXMcpAuthorizer authorizer,
        Func<Task<bool>>? hasUnsavedDocuments = null, IPluginEditorAccess? editor = null)
    {
        this.services = services;
        this.project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        if (!Path.IsPathFullyQualified(project) || !Directory.Exists(this.project) ||
            (File.GetAttributes(this.project) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PLUGIN_PROJECT", "插件需要绑定真实的绝对工程目录。");
        }
        this.authorizer = authorizer;
        this.hasUnsavedDocuments = hasUnsavedDocuments;
        this.editor = editor;
    }

    public event EventHandler<PluginHostOperation>? OperationCompleted;

    public async Task<JsonElement> CallAsync(string pluginId, string tool, JsonElement arguments,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        PackValidator.Token(pluginId);
        PluginContributionValidator.ValidateJson(arguments);
        if (arguments.ValueKind != JsonValueKind.Object || tool.StartsWith("plugin_", StringComparison.Ordinal))
        {
            throw new StudioXException("PLUGIN_HOST_TOOL", "主机参数必须为对象，不能递归调用插件工具。");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var cancellation = linked.Token;
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (stopped.Contains(pluginId))
            {
                throw new StudioXException("PLUGIN_INACTIVE", "插件会话已经停止。");
            }
        }
        finally
        {
            gate.Release();
        }
        JsonElement result;
        if (tool is "editor_read" or "editor_open")
        {
            result = await CallEditorAsync(tool, arguments, cancellation).ConfigureAwait(false);
        }
        else
        {
            var session = await GetSessionAsync(pluginId, cancellation).ConfigureAwait(false);
            var available = await session.ListToolsAsync(cancellation).ConfigureAwait(false);
            if (!available.Any(item => item.Name == tool))
            {
                throw new StudioXException("PLUGIN_HOST_TOOL", "未开放主机工具：" + tool);
            }
            var response = await session.CallToolDetailedAsync(tool, arguments.GetRawText(), cancellation).ConfigureAwait(false);
            JsonElement text;
            try
            {
                text = JsonSerializer.Deserialize<JsonElement>(response.Text);
            }
            catch (JsonException)
            {
                text = JsonSerializer.SerializeToElement(new
                {
                    text = response.Text
                });
            }
            result = response.Images.Count == 0 ? text : JsonSerializer.SerializeToElement(new
            {
                result = text,
                images = response.Images.Select(image => new { image.MimeType, data = Convert.ToBase64String(image.Data) })
            });
        }
        cancellation.ThrowIfCancellationRequested();
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("error", out _))
        {
            OperationCompleted?.Invoke(this, new(pluginId, tool, arguments.Clone(), result.Clone()));
        }
        return result;
    }

    private async Task<StudioXMcpSession> GetSessionAsync(string pluginId, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (stopped.Contains(pluginId))
            {
                throw new StudioXException("PLUGIN_INACTIVE", "插件会话已经停止。");
            }
            if (sessions.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }
            // 关闭插件发现以打断 broker -> MCP -> 插件初始化的递归。
            var tools = new StudioXMcpTools(services, project, new PluginAuthorizer(pluginId, authorizer),
                hasUnsavedDocuments, includePlugins: false);
            var session = await StudioXMcpSession.CreateAsync(tools, token).ConfigureAwait(false);
            sessions.Add(pluginId, session);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>停用立即禁止新操作，然后释放这个插件持有的设备和读取授权。</summary>
    public async Task StopPluginAsync(string pluginId)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        StudioXMcpSession? session;
        try
        {
            stopped.Add(pluginId);
            sessions.Remove(pluginId, out session);
        }
        finally
        {
            gate.Release();
        }
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> CallEditorAsync(string tool, JsonElement arguments, CancellationToken token)
    {
        var relative = arguments.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString()! : "";
        if (!McpWorkspacePathPolicy.IsWorkspaceSourceFile(relative))
        {
            throw new StudioXException("PLUGIN_EDITOR_PATH", "编辑器路径必须是本工程内的安全源码。");
        }
        var full = PathBoundary.Resolve(project, relative);
        if (tool == "editor_open")
        {
            if (editor is null)
            {
                throw new StudioXException("PLUGIN_EDITOR_UNAVAILABLE", "外部 MCP 会话没有可定位的桌面编辑器。");
            }
            if (!File.Exists(full))
            {
                throw new StudioXException("PLUGIN_EDITOR_PATH", "源码不存在。");
            }
            var line = arguments.TryGetProperty("line", out var lineValue) ? lineValue.GetInt32() : 1;
            var column = arguments.TryGetProperty("column", out var columnValue) ? columnValue.GetInt32() : 1;
            if (line is < 1 or > 1000000 || column is < 1 or > 1000000)
            {
                throw new StudioXException("PLUGIN_EDITOR_POSITION", "行列位置超出范围。");
            }
            await editor.OpenAsync(relative, line, column, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new
            {
                opened = relative,
                line,
                column
            });
        }
        var snapshot = editor is null ? null : await editor.ReadAsync(relative, token).ConfigureAwait(false);
        if (snapshot is null)
        {
            var document = await services.Files.ReadAsync(project, relative, token).ConfigureAwait(false);
            snapshot = new(relative, document.Text, false, document.DiskHash);
        }
        var offset = arguments.TryGetProperty("offset", out var offsetValue) ? offsetValue.GetInt32() : 0;
        if (offset < 0 || offset > snapshot.Text.Length)
        {
            throw new StudioXException("PLUGIN_EDITOR_OFFSET", "缓冲区偏移无效。");
        }
        if (offset > 0 && offset < snapshot.Text.Length && char.IsLowSurrogate(snapshot.Text[offset]) &&
            char.IsHighSurrogate(snapshot.Text[offset - 1]))
        {
            throw new StudioXException("PLUGIN_EDITOR_OFFSET", "缓冲区偏移不能拆开 Unicode 字符，请使用 nextOffset。");
        }
        var end = Math.Min(offset + 16000, snapshot.Text.Length);
        if (end < snapshot.Text.Length && end > offset && char.IsHighSurrogate(snapshot.Text[end - 1]) &&
            char.IsLowSurrogate(snapshot.Text[end]))
        {
            end--;
        }
        var text = snapshot.Text[offset..end];
        return JsonSerializer.SerializeToElement(new
        {
            snapshot.RelativePath,
            snapshot.IsDirty,
            snapshot.DiskHash,
            text,
            offset,
            contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Text))),
            totalCharacters = snapshot.Text.Length,
            nextOffset = offset + text.Length < snapshot.Text.Length ? (int?)(offset + text.Length) : null
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var cleanup = new ResourceCleanup();
            foreach (var session in sessions.Values)
            {
                await cleanup.RunAsync(session.DisposeAsync).ConfigureAwait(false);
            }
            sessions.Clear();
            cleanup.ThrowIfFailed("插件主机工具会话清理失败。");
        }
        finally
        {
            gate.Release();
            lifetime.Dispose();
        }
    }

    private sealed class PluginAuthorizer(string id, IStudioXMcpAuthorizer inner) : IStudioXMcpAuthorizer
    {
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token) =>
            inner.ApproveAsync(request with
            {
                Summary = $"插件 {id}\n{request.Summary}"
            }, token);
    }
}

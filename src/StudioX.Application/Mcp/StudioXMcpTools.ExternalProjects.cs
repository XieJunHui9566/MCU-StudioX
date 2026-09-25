namespace StudioX.Application.Mcp;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;

/// <summary>经用户选定的外部工程只作只读参考；复制只能落入当前绑定工程。</summary>
public sealed partial class StudioXMcpTools
{
    private const int ExternalReadBytes = 1024 * 1024;
    private const int ExternalReadChars = 16_000;
    private const int ExternalCopyFiles = 2_000;
    private const long ExternalCopyBytes = 128L * 1024 * 1024;
    private const long ExternalCopySingleFileBytes = 16L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, string> externalRoots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim externalOpenGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ExternalBrowseCursor> externalBrowseCursors = new(StringComparer.Ordinal);
    private const int ExternalBrowseCursorLimit = 32;
    private const int ExternalBrowsePageChars = 20_000;
    private static readonly TimeSpan ExternalBrowseCursorLifetime = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> ExternalExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".studiox", ".build", ".vscode", ".settings", ".cache", "build", "bin", "obj",
        "debug", "release", "node_modules", "artifacts", "secrets", "credentials", "private", "keys", "certs"
    };
    private static readonly HashSet<string> ExternalTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".s", ".asm", ".inc",
        ".ld", ".icf", ".cmake", ".ioc", ".json", ".txt", ".md", ".py", ".xml",
        ".cfg", ".conf", ".ini", ".csv", ".yml", ".yaml", ".v", ".vh", ".sv", ".svh"
    };
    private static readonly HashSet<string> ExternalBlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".pfx", ".p12", ".pem",
        ".key", ".crt", ".cer", ".db", ".sqlite", ".sqlite3"
    };

    [McpServerTool(Name = "external_project_open")]
    [Description("经用户确认后，把一个绝对路径的外部工程或通用库目录作为本次 MCP 会话的只读参考。返回 rootId；不能写入外部目录。")]
    public async Task<string> ExternalProjectOpenAsync(
        [Description("外部工程或通用库的绝对目录；授权卡会显示完整路径。")]
        string directory, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var root = await Task.Run(() => ValidateExternalRoot(directory), cancellationToken).ConfigureAwait(false);
        if (root.Equals(Project, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("MCP_EXTERNAL_ROOT", "当前工程请使用 project_* 工具。");
        await externalOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 同一会话中再次打开同一目录时复用已授权标识，避免重复询问和耗尽目录槽位。
            var existing = externalRoots.FirstOrDefault(item =>
                item.Value.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (existing.Key is not null)
                return JsonSerializer.Serialize(new { rootId = existing.Key, directory = root, readOnly = true,
                    alreadyApproved = true });
            if (externalRoots.Count >= 8)
                throw new StudioXException("MCP_EXTERNAL_ROOTS", "本次会话最多同时读取 8 个外部目录。");
            await RequireApprovalAsync("external_project_open",
                $"允许本次工程 MCP 会话只读浏览、搜索和读取外部目录：{root}。仅此目录内的安全文件可见；复制到当前工程仍会逐次请求写入授权。",
                StudioXMcpPermission.ExternalRead, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = await Task.Run(() => ValidateExternalRoot(root), cancellationToken).ConfigureAwait(false);
            var rootId = Guid.NewGuid().ToString("N");
            externalRoots[rootId] = root;
            return JsonSerializer.Serialize(new { rootId, directory = root, readOnly = true,
                alreadyApproved = false });
        }
        finally { externalOpenGate.Release(); }
    }

    [McpServerTool(Name = "external_project_roots")]
    [Description("列出当前 MCP 会话已授权的外部只读目录及 rootId；不会新增授权。")]
    public async Task<string> ExternalProjectRootsAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { roots = externalRoots.OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { rootId = item.Key, directory = item.Value, readOnly = true }).ToArray() });
    }

    [McpServerTool(Name = "external_project_list_files")]
    [Description("分页列出已授权外部目录的下一层安全文件和目录；将 nextCursor 传给下次调用继续。")]
    public async Task<string> ExternalProjectListFilesAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("授权目录内正斜杠分隔的相对目录，留空表示根目录。")]
        string directory = "",
        [Description("上次返回的 nextCursor；首次调用留空。")]
        string cursor = "", CancellationToken cancellationToken = default)
    {
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var path = ResolveExternalPath(root, directory, allowRoot: true);
        if (!Directory.Exists(path)) throw new StudioXException("MCP_EXTERNAL_DIRECTORY", "外部目录不存在。");
        var browse = GetExternalBrowseCursor(rootId, root, "list", directory, "", cursor);
        await browse.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureExternalBrowseActive(browse);
            var entries = new List<object>();
            var visited = 0;
            var pageChars = 0;
            while (entries.Count < 80 && visited < 1_000 && TryNextExternalEntry(browse, out var entry, out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visited++;
                entry.Refresh();
                if (IsExcludedExternalEntry(entry)) continue;
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isDirectory && !IsExternalCopyFile(entry.Name)) continue;
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                _ = ResolveExternalPath(root, relative);
                var entryChars = JsonSerializer.Serialize(relative).Length + 160;
                if (entries.Count > 0 && pageChars + entryChars > ExternalBrowsePageChars)
                {
                    browse.PendingEntry = new ExternalPendingEntry(entry, frame);
                    break;
                }
                pageChars += entryChars;
                entries.Add(new
                {
                    path = relative,
                    directory = isDirectory,
                    readable = !isDirectory && IsExternalTextFile(entry.Name),
                    bytes = isDirectory ? (long?)null : ((FileInfo)entry).Length
                });
            }
            var nextCursor = FinishExternalBrowsePage(browse);
            return JsonSerializer.Serialize(new { rootId, directory, scannedEntries = visited,
                truncated = nextCursor is not null, nextCursor, entries });
        }
        catch
        {
            RetireExternalBrowseCursor(browse);
            throw;
        }
        finally { browse.Gate.Release(); }
    }

    [McpServerTool(Name = "external_project_find_files")]
    [Description("按文件名或相对路径子串分页查找已授权外部目录；只返回路径、目录标志及大小。nextCursor 可继续扫描。")]
    public async Task<string> ExternalProjectFindFilesAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("文件名或相对路径中要查找的文字，2–120 个字符，不区分大小写。")]
        string query,
        [Description("授权目录内正斜杠分隔的相对目录，留空表示整个外部目录。")]
        string directory = "",
        [Description("最多返回 1–80 个结果。")]
        int maxResults = 40,
        [Description("上次返回的 nextCursor；首次调用留空。")]
        string cursor = "",
        CancellationToken cancellationToken = default)
    {
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var needle = query?.Trim().Replace('\\', '/');
        if (needle is null || needle.Length is < 2 or > 120 || needle.Any(char.IsControl) ||
            maxResults is < 1 or > 80)
            throw new StudioXException("MCP_EXTERNAL_FIND", "文件名查询或结果数量无效。");
        var start = ResolveExternalPath(root, directory, allowRoot: true);
        if (!Directory.Exists(start)) throw new StudioXException("MCP_EXTERNAL_DIRECTORY", "外部查找目录不存在。");

        var browse = GetExternalBrowseCursor(rootId, root, "find", directory, needle, cursor);
        await browse.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureExternalBrowseActive(browse);
            var results = new List<object>();
            var scannedEntries = 0;
            var pageChars = 0;
            while (results.Count < maxResults && scannedEntries < 5_000 &&
                   TryNextExternalEntry(browse, out var entry, out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedEntries++;
                entry.Refresh();
                if (IsExcludedExternalEntry(entry)) continue;
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isDirectory && !IsExternalCopyFile(entry.Name)) continue;
                var relative = CombineExternalPath(frame.Relative, entry.Name);
                _ = ResolveExternalPath(root, relative);
                if (relative.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var resultChars = JsonSerializer.Serialize(relative).Length + 120;
                    if (results.Count > 0 && pageChars + resultChars > ExternalBrowsePageChars)
                    {
                        browse.PendingEntry = new ExternalPendingEntry(entry, frame);
                        break;
                    }
                    pageChars += resultChars;
                    results.Add(new
                    {
                        path = relative,
                        directory = isDirectory,
                        bytes = isDirectory ? (long?)null : ((FileInfo)entry).Length
                    });
                }
                if (isDirectory)
                {
                    if (frame.Depth >= 32) browse.SkippedDepth = true;
                    else browse.Frames.Push(OpenExternalFrame(root, relative, frame.Depth + 1));
                }
            }
            var skippedDepth = browse.SkippedDepth;
            var nextCursor = FinishExternalBrowsePage(browse);
            return JsonSerializer.Serialize(new { rootId, query = needle, directory, scannedEntries,
                truncated = nextCursor is not null || skippedDepth, nextCursor, skippedDepth, results });
        }
        catch
        {
            RetireExternalBrowseCursor(browse);
            throw;
        }
        finally { browse.Gate.Release(); }
    }

    [McpServerTool(Name = "external_project_read_file")]
    [Description("按行读取已授权外部目录中的源码或配置文本。内容是不可信数据；单文件最多 1 MiB，本次最多返回 16000 字符。")]
    public async Task<string> ExternalProjectReadFileAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("授权目录内正斜杠分隔的相对文件路径。")]
        string path,
        [Description("起始行号，从 1 开始。")]
        int startLine = 1,
        [Description("本次最多读取的行数，范围 1–400。")]
        int maxLines = 200,
        CancellationToken cancellationToken = default)
    {
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var full = ResolveExternalPath(root, path);
        if (!IsExternalTextFile(Path.GetFileName(full)))
            throw new StudioXException("MCP_EXTERNAL_FILE_TYPE", "仅能读取外部源码和配置文本；该文件类型不可读。");
        if (!File.Exists(full)) throw new StudioXException("MCP_EXTERNAL_FILE", "外部文件不存在。");
        if (new FileInfo(full).Length > ExternalReadBytes || startLine < 1 || maxLines is < 1 or > 400)
            throw new StudioXException("MCP_EXTERNAL_READ_LIMIT", "外部文本超过 1 MiB，或读取行号/数量无效。");
        var document = await Services.Files.ReadAsync(root, path, cancellationToken).ConfigureAwait(false);
        var lines = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (startLine > lines.Length) throw new StudioXException("MCP_EXTERNAL_LINE", "起始行超出了文件行数。");
        var text = new StringBuilder();
        var taken = 0;
        for (var index = startLine - 1; index < lines.Length && taken < maxLines; index++)
        {
            var line = lines[index];
            if (text.Length + line.Length + 1 > ExternalReadChars) break;
            if (taken > 0) text.Append('\n');
            text.Append(line);
            taken++;
        }
        if (taken == 0)
            throw new StudioXException("MCP_EXTERNAL_LINE", "单行文本超过 MCP 返回上限，请缩小文件范围或使用其他编辑器查看。");
        return JsonSerializer.Serialize(new
        {
            rootId, path, startLine, nextLine = startLine - 1 + taken < lines.Length ? startLine + taken : (int?)null,
            totalLines = lines.Length, truncated = startLine - 1 + taken < lines.Length,
            sha256 = document.DiskHash, readOnly = true, text = text.ToString()
        });
    }

    [McpServerTool(Name = "external_project_search")]
    [Description("分页搜索已授权外部目录的源码和配置文本；不会读取隐藏、构建或凭据目录。nextCursor 可继续扫描。")]
    public async Task<string> ExternalProjectSearchAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("搜索文字，至少 2 个字符，最多 120 个字符。")]
        string query,
        [Description("授权目录内相对目录，留空表示整个外部目录。")]
        string directory = "",
        [Description("最多返回 1–60 处匹配。")]
        int maxResults = 40,
        [Description("上次返回的 nextCursor；首次调用留空。")]
        string cursor = "",
        CancellationToken cancellationToken = default)
    {
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 120 || query.Any(char.IsControl) ||
            maxResults is < 1 or > 60)
            throw new StudioXException("MCP_EXTERNAL_SEARCH", "搜索文字或结果数量无效。");
        var start = ResolveExternalPath(root, directory, allowRoot: true);
        if (!Directory.Exists(start)) throw new StudioXException("MCP_EXTERNAL_DIRECTORY", "外部搜索目录不存在。");
        var browse = GetExternalBrowseCursor(rootId, root, "search", directory, query, cursor);
        await browse.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureExternalBrowseActive(browse);
            var results = new List<object>();
            var visited = 0;
            var scanned = 0;
            long scannedBytes = 0;
            var pageChars = 0;
            while (results.Count < maxResults && visited < 2_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (browse.PendingLines is { } pendingLines)
                {
                    while (pendingLines.NextIndex < pendingLines.Lines.Length && results.Count < maxResults)
                    {
                        var index = pendingLines.NextIndex;
                        var line = pendingLines.Lines[index];
                        if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var preview = line.Length <= 200 ? line : line[..200];
                            var resultChars = JsonSerializer.Serialize(pendingLines.Relative).Length +
                                JsonSerializer.Serialize(preview).Length + 120;
                            if (results.Count > 0 &&
                                pageChars + resultChars > ExternalBrowsePageChars)
                                break;
                            pageChars += resultChars;
                            results.Add(new { path = pendingLines.Relative, line = index + 1, text = preview });
                        }
                        pendingLines.NextIndex++;
                    }
                    if (pendingLines.NextIndex < pendingLines.Lines.Length && results.Count < maxResults) break;
                    if (pendingLines.NextIndex == pendingLines.Lines.Length) browse.PendingLines = null;
                    continue;
                }
                string relative;
                if (browse.PendingFile is { } pendingFile)
                {
                    relative = pendingFile;
                    browse.PendingFile = null;
                }
                else
                {
                    if (!TryNextExternalEntry(browse, out var entry, out var frame)) break;
                    visited++;
                    entry.Refresh();
                    if (IsExcludedExternalEntry(entry)) continue;
                    relative = CombineExternalPath(frame.Relative, entry.Name);
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (frame.Depth >= 32) browse.SkippedDepth = true;
                        else browse.Frames.Push(OpenExternalFrame(root, relative, frame.Depth + 1));
                        continue;
                    }
                    if (!IsExternalTextFile(entry.Name)) continue;
                }
                var full = ResolveExternalPath(root, relative);
                if (!File.Exists(full)) continue;
                var size = new FileInfo(full).Length;
                if (size > 128 * 1024) continue;
                if (scannedBytes + size > 16L * 1024 * 1024)
                {
                    browse.PendingFile = relative;
                    break;
                }
                scannedBytes += size;
                scanned++;
                try
                {
                    var document = await Services.Files.ReadAsync(root, relative, cancellationToken).ConfigureAwait(false);
                    browse.PendingLines = new ExternalPendingLines(relative,
                        document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
                }
                catch (StudioXException ex) when (ex.Code is "EDITOR_ENCODING" or "EDITOR_BINARY") { }
            }
            var skippedDepth = browse.SkippedDepth;
            var nextCursor = FinishExternalBrowsePage(browse);
            return JsonSerializer.Serialize(new { rootId, query, directory, scannedFiles = scanned,
                scannedEntries = visited, truncated = nextCursor is not null || skippedDepth,
                nextCursor, skippedDepth, results });
        }
        catch
        {
            RetireExternalBrowseCursor(browse);
            throw;
        }
        finally { browse.Gate.Release(); }
    }

    [McpServerTool(Name = "external_project_copy")]
    [Description("把已授权外部目录本身、其中单个文件或通用库文件夹复制进当前绑定工程。会跳过隐藏/构建/凭据项；逐次请求写入授权，禁止覆盖，最多 2000 文件/128 MiB。")]
    public async Task<string> ExternalProjectCopyAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("授权外部目录内的相对文件或文件夹路径；空字符串表示复制获批的整个目录。")]
        string sourcePath,
        [Description("当前绑定工程内的目标相对路径；文件指定文件名，文件夹指定目标文件夹名。")]
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var destination = ResolveCopyDestination(destinationPath);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new StudioXException("MCP_EXTERNAL_COPY_EXISTS", "目标已存在；不会覆盖或自动改名。");
        var plan = await Task.Run(() => PlanExternalCopy(root, sourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("external_project_copy",
            $"从只读示例 {source} 复制到当前工程 {destination}。{plan.Files.Count} 个文件 / {plan.TotalBytes} 字节；" +
            $"内容清单 SHA-256 {plan.Sha256}；跳过 {plan.Skipped} 个隐藏、构建、凭据或链接项。仅新建，不覆盖；不会自动编译或烧录。",
            StudioXMcpPermission.FileWrite, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var current = await Task.Run(() => PlanExternalCopy(root, sourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!current.Sha256.Equals(plan.Sha256, StringComparison.Ordinal) ||
            current.Skipped != plan.Skipped || current.Files.Count != plan.Files.Count ||
            !current.Directories.SequenceEqual(plan.Directories, StringComparer.Ordinal))
            throw new StudioXException("MCP_EXTERNAL_CHANGED", "审批期间外部文件发生变化，请重新检查后复制。");
        destination = ResolveCopyDestination(destinationPath);
        await StageExternalCopyAsync(root, sourcePath, destinationPath, destination, current, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            source = sourcePath, destination = destinationPath, copiedFiles = current.Files.Count,
            bytes = current.TotalBytes, sha256 = current.Sha256, skippedEntries = current.Skipped, created = true
        });
    }

    private sealed class ExternalBrowseFrame(string relative, int depth, IEnumerator<FileSystemInfo> entries)
        : IDisposable
    {
        public string Relative { get; } = relative;
        public int Depth { get; } = depth;
        public IEnumerator<FileSystemInfo> Entries { get; } = entries;
        public void Dispose() => Entries.Dispose();
    }

    private sealed class ExternalPendingLines(string relative, string[] lines)
    {
        public string Relative { get; } = relative;
        public string[] Lines { get; } = lines;
        public int NextIndex { get; set; }
    }

    private sealed record ExternalPendingEntry(FileSystemInfo Entry, ExternalBrowseFrame Frame);

    private sealed class ExternalBrowseCursor(string id, string rootId, string root, string operation,
        string directory, string query) : IDisposable
    {
        public string Id { get; } = id;
        public string RootId { get; } = rootId;
        public string Root { get; } = root;
        public string Operation { get; } = operation;
        public string Directory { get; } = directory;
        public string Query { get; } = query;
        public Stack<ExternalBrowseFrame> Frames { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? PendingFile { get; set; }
        public ExternalPendingEntry? PendingEntry { get; set; }
        public ExternalPendingLines? PendingLines { get; set; }
        public bool SkippedDepth { get; set; }
        public bool Completed { get; set; }

        public void Dispose()
        {
            Completed = true;
            PendingFile = null;
            PendingEntry = null;
            PendingLines = null;
            while (Frames.TryPop(out var frame)) frame.Dispose();
        }
    }

    private ExternalBrowseCursor GetExternalBrowseCursor(string rootId, string root, string operation,
        string directory, string query, string cursor)
    {
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!externalBrowseCursors.TryGetValue(cursor, out var existing) ||
                existing.RootId != rootId || existing.Root != root || existing.Operation != operation ||
                existing.Directory != directory || existing.Query != query)
                throw new StudioXException("MCP_EXTERNAL_CURSOR", "外部目录游标无效或查询参数已改变，请从第一页重新查找。");
            return existing;
        }
        PruneExternalBrowseCursors();
        if (externalBrowseCursors.Count >= ExternalBrowseCursorLimit)
            throw new StudioXException("MCP_EXTERNAL_CURSOR", "外部目录游标正在被并发使用，请稍后重试。");
        var id = Guid.NewGuid().ToString("N");
        var created = new ExternalBrowseCursor(id, rootId, root, operation, directory, query);
        created.Frames.Push(OpenExternalFrame(root, directory, 0));
        externalBrowseCursors[id] = created;
        return created;
    }

    private void PruneExternalBrowseCursors()
    {
        var oldest = DateTimeOffset.UtcNow - ExternalBrowseCursorLifetime;
        foreach (var (id, browse) in externalBrowseCursors
                     .OrderBy(item => item.Value.LastUsedUtc).ToArray())
        {
            if (browse.LastUsedUtc >= oldest && externalBrowseCursors.Count < ExternalBrowseCursorLimit) break;
            if (!browse.Gate.Wait(0)) continue;
            try
            {
                if ((browse.LastUsedUtc < oldest || externalBrowseCursors.Count >= ExternalBrowseCursorLimit) &&
                    externalBrowseCursors.TryRemove(id, out var removed))
                    removed.Dispose();
            }
            finally { browse.Gate.Release(); }
        }
    }

    private static ExternalBrowseFrame OpenExternalFrame(string root, string relative, int depth)
    {
        var path = RequireExternalBrowseDirectory(root, relative);
        return new ExternalBrowseFrame(relative, depth,
            new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator());
    }

    private static string RequireExternalBrowseDirectory(string root, string relative)
    {
        var path = ResolveExternalPath(root, relative, allowRoot: true);
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
            throw new StudioXException("MCP_EXTERNAL_DIRECTORY", "外部目录不存在或在扫描期间变成隐藏、系统、链接目录。");
        return path;
    }

    private static bool TryNextExternalEntry(ExternalBrowseCursor browse,
        out FileSystemInfo entry, out ExternalBrowseFrame frame)
    {
        if (browse.PendingEntry is { } pending)
        {
            browse.PendingEntry = null;
            frame = pending.Frame;
            _ = RequireExternalBrowseDirectory(browse.Root, frame.Relative);
            entry = pending.Entry;
            entry.Refresh();
            return true;
        }
        while (browse.Frames.TryPeek(out frame!))
        {
            // 每次继续枚举前重新核对父目录，避免会话期间替换成重解析点。
            _ = RequireExternalBrowseDirectory(browse.Root, frame.Relative);
            if (frame.Entries.MoveNext())
            {
                entry = frame.Entries.Current;
                return true;
            }
            browse.Frames.Pop().Dispose();
        }
        entry = null!;
        frame = null!;
        return false;
    }

    private static bool IsExcludedExternalEntry(FileSystemInfo entry) =>
        (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0 ||
        IsExcludedExternalName(entry.Name);

    private string? FinishExternalBrowsePage(ExternalBrowseCursor browse)
    {
        browse.LastUsedUtc = DateTimeOffset.UtcNow;
        if (browse.Frames.Count != 0 || browse.PendingEntry is not null ||
            browse.PendingFile is not null || browse.PendingLines is not null)
            return browse.Id;
        _ = externalBrowseCursors.TryRemove(browse.Id, out _);
        browse.Dispose();
        return null;
    }

    private void RetireExternalBrowseCursor(ExternalBrowseCursor browse)
    {
        _ = externalBrowseCursors.TryRemove(browse.Id, out _);
        browse.Dispose();
    }

    private static void EnsureExternalBrowseActive(ExternalBrowseCursor browse)
    {
        if (browse.Completed)
            throw new StudioXException("MCP_EXTERNAL_CURSOR", "外部目录游标已用完或过期，请从第一页重新查找。");
    }

    private async Task DisposeExternalCursorsAsync()
    {
        foreach (var (id, browse) in externalBrowseCursors)
        {
            await browse.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (externalBrowseCursors.TryRemove(id, out var removed)) removed.Dispose();
            }
            finally { browse.Gate.Release(); }
        }
    }

    private async Task<string> RequireExternalRootAsync(string rootId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(rootId) || !externalRoots.TryGetValue(rootId, out var root))
            throw new StudioXException("MCP_EXTERNAL_GRANT", "外部目录未获得本次 MCP 会话授权，请先调用 external_project_open。");
        return await Task.Run(() => ValidateExternalRoot(root), token).ConfigureAwait(false);
    }

    private static string ValidateExternalRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 2_048 || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith("\\\\?\\", StringComparison.Ordinal) || directory.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new StudioXException("MCP_EXTERNAL_ROOT", "外部目录必须是普通绝对路径。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root) ||
            IsExcludedExternalName(Path.GetFileName(root)))
            throw new StudioXException("MCP_EXTERNAL_ROOT", "请选择存在的工程或通用库目录，不能选择磁盘根目录或受保护目录。");
        EnsureNoReparseAncestors(root);
        return root;
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(current));
            if (name.Length > 0 && IsExcludedExternalName(name))
                throw new StudioXException("MCP_EXTERNAL_ROOT", "外部目录位于隐藏、构建或凭据目录内，不能授权读取。");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("MCP_EXTERNAL_LINK", "外部目录或其父目录含符号链接/重解析点，不能授权读取。");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static string ResolveExternalPath(string root, string relative, bool allowRoot = false)
    {
        if (allowRoot && relative.Length == 0) return root;
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1_024 ||
            relative.Split('/').Any(IsExcludedExternalName))
            throw new StudioXException("MCP_EXTERNAL_PATH", "外部路径为空、过长或包含受保护目录/文件。");
        return PathBoundary.Resolve(root, relative);
    }

    private string ResolveCopyDestination(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1_024 ||
            relative.Split('/').Any(IsExcludedExternalName) ||
            relative.Split('/')[0].Equals("device", StringComparison.OrdinalIgnoreCase) ||
            !ProjectFileService.CanCreateIn(relative))
            throw new StudioXException("MCP_EXTERNAL_DESTINATION", "只能复制到当前工程的用户源码目录，不能写入受保护目录。");
        return PathBoundary.Resolve(Project, relative);
    }

    private static bool IsExcludedExternalName(string name) =>
        name.Length == 0 || name.StartsWith('.') || ExternalExcludedDirectories.Contains(name) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("token.", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternalCopyFile(string name) =>
        !IsExcludedExternalName(name) && !ExternalBlockedExtensions.Contains(Path.GetExtension(name));

    private static bool IsExternalTextFile(string name) =>
        IsExternalCopyFile(name) &&
        (ExternalTextExtensions.Contains(Path.GetExtension(name)) ||
         name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase));

    private sealed record ExternalCopyItem(string Source, string Relative, long Bytes, string Sha256);
    private sealed record ExternalCopyPlan(bool Directory, IReadOnlyList<string> Directories,
        IReadOnlyList<ExternalCopyItem> Files, long TotalBytes, int Skipped, string Sha256);

    private static ExternalCopyPlan PlanExternalCopy(string root, string sourcePath, CancellationToken token)
    {
        var source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var directory = Directory.Exists(source);
        if (!directory && !File.Exists(source)) throw new StudioXException("MCP_EXTERNAL_FILE", "外部复制源不存在。");
        var directories = new List<string>();
        var files = new List<ExternalCopyItem>();
        var skipped = 0;
        var visited = 0;
        long bytes = 0;
        if (directory)
        {
            var pending = new Stack<(string Relative, int Depth)>();
            pending.Push(("", 0));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (parent, depth) = pending.Pop();
                if (depth > 32) throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "通用库目录层级超过 32 层。");
                var current = ResolveExternalPath(root, CombineExternalPath(sourcePath, parent), allowRoot: true);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new StudioXException("MCP_EXTERNAL_LINK", "复制源目录在扫描期间变成了链接，请重新选择。");
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 10_000) throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "通用库目录项超过 10000 项，请缩小复制范围。");
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || IsExcludedExternalName(entry.Name) ||
                        (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    { skipped++; continue; }
                    var relative = parent.Length == 0 ? entry.Name : parent + "/" + entry.Name;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    { directories.Add(relative); pending.Push((relative, depth + 1)); continue; }
                    if (!IsExternalCopyFile(entry.Name)) { skipped++; continue; }
                    AddFile(ResolveExternalPath(root, CombineExternalPath(sourcePath, relative)), relative);
                }
            }
        }
        else
        {
            if (!IsExternalCopyFile(Path.GetFileName(source)))
                throw new StudioXException("MCP_EXTERNAL_FILE_TYPE", "该外部文件属于受保护类型，不能复制。");
            if ((File.GetAttributes(source) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                throw new StudioXException("MCP_EXTERNAL_FILE_TYPE", "该外部文件是隐藏、系统或链接文件，不能复制。");
            AddFile(source, "");
        }
        if (files.Count == 0)
            throw new StudioXException("MCP_EXTERNAL_COPY_EMPTY", "所选目录没有可复制文件，请选择更具体的源码目录。");
        var manifest = string.Join("\n", directories.OrderBy(item => item, StringComparer.Ordinal).Select(item => "D\0" + item)) +
            "\n" + string.Join("\n", files.OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => "F\0" + item.Relative + "\0" + item.Bytes + "\0" + item.Sha256));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        return new ExternalCopyPlan(directory, directories, files, bytes, skipped, hash);

        void AddFile(string path, string relative)
        {
            token.ThrowIfCancellationRequested();
            var length = new FileInfo(path).Length;
            if (length > ExternalCopySingleFileBytes || files.Count >= ExternalCopyFiles ||
                bytes + length > ExternalCopyBytes)
                throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "复制最多 2000 个文件、总计 128 MiB、单文件 16 MiB，请缩小范围。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            files.Add(new ExternalCopyItem(path, relative, length, digest));
            bytes += length;
        }
    }

    private static string CombineExternalPath(string sourcePath, string relative) =>
        sourcePath.Length == 0 ? relative : relative.Length == 0 ? sourcePath : sourcePath + "/" + relative;

    private async Task StageExternalCopyAsync(string root, string sourcePath, string destinationPath,
        string destination, ExternalCopyPlan plan, CancellationToken token)
    {
        var parentRelative = ProjectFileService.ParentDirectory(destinationPath);
        var createdParents = new List<string>();
        string? stage = null;
        var committed = false;
        try
        {
            var parts = parentRelative.Length == 0 ? [] : parentRelative.Split('/');
            for (var index = 1; index <= parts.Length; index++)
            {
                var relative = string.Join('/', parts.Take(index));
                var path = PathBoundary.Resolve(Project, relative);
                if (File.Exists(path)) throw new StudioXException("MCP_EXTERNAL_DESTINATION", "复制目标的父路径不是目录。");
                if (!Directory.Exists(path)) { Directory.CreateDirectory(path); createdParents.Add(path); }
            }
            var parent = parentRelative.Length == 0 ? Project : PathBoundary.Resolve(Project, parentRelative);
            stage = Path.Combine(parent, ".studiox-copy-" + Guid.NewGuid().ToString("N"));
            if (plan.Directory)
            {
                Directory.CreateDirectory(stage);
                foreach (var relative in plan.Directories)
                    Directory.CreateDirectory(PathBoundary.Resolve(stage, relative));
                foreach (var item in plan.Files)
                {
                    var staged = PathBoundary.Resolve(stage, item.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await CopyExternalItemAsync(root, CombineExternalPath(sourcePath, item.Relative), staged, item, token)
                        .ConfigureAwait(false);
                }
            }
            else
                await CopyExternalItemAsync(root, sourcePath, stage, plan.Files[0], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ = ResolveCopyDestination(destinationPath);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new StudioXException("MCP_EXTERNAL_COPY_EXISTS", "目标已被其他操作创建，本次未覆盖。");
            if (plan.Directory) Directory.Move(stage, destination);
            else File.Move(stage, destination);
            committed = true;
        }
        finally
        {
            if (!committed && stage is not null)
            {
                if (File.Exists(stage)) File.Delete(stage);
                else if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
            if (!committed)
                foreach (var parent in createdParents.AsEnumerable().Reverse())
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
    }

    private static async Task CopyExternalItemAsync(string root, string relativeSource, string target,
        ExternalCopyItem planned, CancellationToken token)
    {
        var source = ResolveExternalPath(root, relativeSource);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        using var copied = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (copied.Length != planned.Bytes ||
            !Convert.ToHexString(SHA256.HashData(copied)).Equals(planned.Sha256, StringComparison.Ordinal))
            throw new StudioXException("MCP_EXTERNAL_CHANGED", "外部源文件在复制期间发生变化，目标未提交。");
    }
}

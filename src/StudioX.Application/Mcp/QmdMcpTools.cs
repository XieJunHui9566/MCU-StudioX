namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;
using static McpWorkspacePathPolicy;

/// <summary>管理绑定工程的有界源码快照；工具名保留兼容，检索由内置 C# 实现。</summary>
internal sealed class QmdMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    private const int QmdMaxFiles = 400;
    private const long QmdMaxSourceBytes = 8L * 1024 * 1024;
    private const long QmdMaxFileBytes = 512L * 1024;
    private const long QmdMaxProjectBytes = 64L * 1024 * 1024;
    private const int QmdRetainedProjects = 3;
    private const int QmdMaxOutputChars = 16_000;
    private static readonly UTF8Encoding QmdUtf8 = new(false);
    private const string QmdManagedMarker = "StudioX QMD BM25 cache v1";

    [McpServerTool(Name = "project_qmd_search")]
    [Description("内置 C# BM25 关键词检索当前工程安全源码，返回少量相关片段。首次调用建立有界本地快照，未变更时复用；无需 Node.js、QMD、模型或联网。目录过大时指定更窄的 directory。工具名保留兼容；关键词检索不直接改变 API 前缀缓存命中率。")]
    public async Task<string> ProjectQmdSearchAsync(
        [Description("2–160 字符的关键词或短语。")]
        string query,
        [Description("当前工程内正斜杠分隔的相对目录；留空检索全工程。")]
        string directory = "",
        [Description("返回 1–10 个结果，默认 6 个。")]
        int maxResults = 6,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 160 ||
            query.Any(char.IsControl) || maxResults is < 1 or > 10)
        {
            throw new StudioXException("MCP_QMD_QUERY", "QMD 检索词或结果数量无效。");
        }
        if (directory.Length > 0)
        {
            RequireWorkspaceSourceDirectory(directory);
        }
        SourceBm25Search.ValidateQuery(query, cancellationToken);
        var selectedRoot = directory.Length == 0 ? Project : PathBoundary.Resolve(Project, directory);
        if (!Directory.Exists(selectedRoot))
        {
            throw new StudioXException("MCP_QMD_DIRECTORY", "QMD 检索目录不存在。");
        }

        var projectKey = Convert.ToHexString(SHA256.HashData(QmdUtf8.GetBytes(Project.ToUpperInvariant())))[..20];
        var qmdRoot = PathBoundary.Resolve(Services.DataDirectory, "qmd-bm25");
        Directory.CreateDirectory(qmdRoot);
        await using var globalGate = await AcquireQmdLockAsync(qmdRoot, cancellationToken).ConfigureAwait(false);
        var indexRoot = PathBoundary.Resolve(qmdRoot, projectKey);
        if (!Directory.Exists(indexRoot))
        {
            Directory.CreateDirectory(indexRoot);
        }
        else
        {
            var marker = Path.Combine(indexRoot, "managed.marker");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 ||
                (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(marker, QmdUtf8) != QmdManagedMarker)
            {
                throw new StudioXException("MCP_QMD_INDEX_PATH", "QMD 专用索引目录存在非 StudioX 数据，拒绝覆盖。");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(indexRoot, "managed.marker"), QmdManagedMarker,
            QmdUtf8, cancellationToken).ConfigureAwait(false);
        PruneQmdProjectIndexes(qmdRoot, indexRoot);
        await using var gate = await AcquireQmdLockAsync(indexRoot, cancellationToken).ConfigureAwait(false);
        var mirror = Path.Combine(indexRoot, "source");
        // 旧版外部后端的数据库与配置不再使用；只清理已核对归属的托管缓存。
        foreach (var legacy in new[] { "config", "cache" })
        {
            var legacyPath = PathBoundary.Resolve(indexRoot, legacy);
            if (Directory.Exists(legacyPath))
            {
                DeleteManagedQmdDirectory(indexRoot, legacyPath);
            }
        }

        var candidates = CollectQmdSources(selectedRoot, directory, cancellationToken);
        var statePath = Path.Combine(indexRoot, "snapshot.json");
        QmdSnapshotState? priorState = null;
        if (File.Exists(statePath) &&
            (File.GetAttributes(statePath) & FileAttributes.ReparsePoint) == 0 &&
            new FileInfo(statePath).Length <= 256 * 1024)
        {
            try
            {
                priorState = JsonSerializer.Deserialize<QmdSnapshotState>(
                await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false));
            }
            catch (JsonException) { /* 状态损坏时重新建索引。 */ }
        }
        var reusable = priorState?.Paths is not null && priorState.Directory == directory &&
            priorState.Fingerprint == candidates.Fingerprint && Directory.Exists(mirror) &&
            await ValidateQmdSnapshotAsync(priorState, candidates, mirror, cancellationToken)
                .ConfigureAwait(false);
        QmdSnapshot snapshot;
        if (reusable)
        {
            snapshot = new QmdSnapshot(priorState!.Files, priorState.Bytes,
            priorState.SkippedOversize, priorState.Paths);
        }
        else
        {
            // 仅在目录或文件变化时重建；已删文件不会继续命中。
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
            if (Directory.Exists(mirror))
            {
                DeleteManagedQmdDirectory(indexRoot, mirror);
            }
            Directory.CreateDirectory(mirror);
            snapshot = await CreateQmdSnapshotAsync(candidates, mirror, cancellationToken)
                .ConfigureAwait(false);
        }
        if (snapshot.Files == 0)
        {
            return JsonSerializer.Serialize(new
            {
                query,
                directory,
                indexedFiles = 0,
                skippedOversizeFiles = snapshot.SkippedOversize,
                results = Array.Empty<object>(),
                note = "所选目录没有可索引的安全源码；可使用 project_search 或缩小目录。"
            });
        }

        if (!reusable)
        {
            await File.WriteAllTextAsync(statePath,
                JsonSerializer.Serialize(new QmdSnapshotState(directory, candidates.Fingerprint,
                    snapshot.Files, snapshot.Bytes, snapshot.SkippedOversize,
                    new Dictionary<string, string>(snapshot.Paths, StringComparer.Ordinal))), QmdUtf8,
                cancellationToken).ConfigureAwait(false);
        }
        if (DirectorySize(indexRoot) > QmdMaxProjectBytes)
        {
            throw new StudioXException("MCP_QMD_INDEX_SIZE", "项目检索缓存超过 64 MiB，请缩小检索目录。");
        }
        var matches = await SourceBm25Search.SearchAsync(mirror, snapshot.Paths, query, maxResults,
            cancellationToken).ConfigureAwait(false);
        var output = JsonSerializer.Serialize(new
        {
            query,
            directory,
            engine = "studiox-csharp-bm25",
            indexedFiles = snapshot.Files,
            indexedSourceBytes = snapshot.Bytes,
            skippedOversizeFiles = snapshot.SkippedOversize,
            reusedIndex = reusable,
            results = matches.Select(item => new { path = item.Path, line = item.Line, score = item.Score, snippet = item.Snippet })
        });
        if (output.Length > QmdMaxOutputChars)
        {
            throw new StudioXException("MCP_QMD_OUTPUT", "检索结果超过输出上限，请减少 maxResults。");
        }
        return output;
    }

    /// <summary>先按工程源码策略筛选并生成目录指纹，超出范围时拒绝创建索引。</summary>
    private QmdSources CollectQmdSources(string selectedRoot, string directory,
        CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(selectedRoot);
        var files = new List<QmdSourceFile>();
        var bytes = 0L;
        var skippedOversize = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }
                var relative = Path.GetRelativePath(Project, path).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsWorkspaceSourceDirectory(relative))
                    {
                        pending.Push(path);
                    }
                    continue;
                }
                if (!IsWorkspaceSourceFile(relative))
                {
                    continue;
                }
                var length = new FileInfo(path).Length;
                if (length > QmdMaxFileBytes)
                {
                    skippedOversize++;
                    continue;
                }
                if (files.Count + 1 > QmdMaxFiles || bytes + length > QmdMaxSourceBytes)
                {
                    throw new StudioXException("MCP_QMD_SCOPE",
                    "QMD 检索范围超过 400 个文件或 8 MiB 源码，请用 directory 指定更小的目录；未创建大索引。");
                }
                bytes += length;
                files.Add(new QmdSourceFile(relative, File.GetLastWriteTimeUtc(path).Ticks, length));
            }
        }
        files.Sort((left, right) => string.Compare(left.Relative, right.Relative, StringComparison.Ordinal));
        var signature = new StringBuilder(directory).Append('\n');
        foreach (var file in files)
        {
            signature.Append(file.Relative).Append('|').Append(file.ModifiedTicks).Append('|')
            .Append(file.Length).Append('\n');
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(QmdUtf8.GetBytes(signature.ToString())));
        return new QmdSources(files, fingerprint, skippedOversize);
    }

    /// <summary>使用固定镜像名，映射仅指向校验过的工程文件。</summary>
    private async Task<QmdSnapshot> CreateQmdSnapshotAsync(QmdSources candidates, string mirror,
        CancellationToken token)
    {
        var files = 0;
        var bytes = 0L;
        var skippedOversize = candidates.SkippedOversize;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Files)
        {
            token.ThrowIfCancellationRequested();
            var relative = candidate.Relative;
            StudioX.Application.SourceDocument source;
            try
            {
                source = await Services.Files.ReadAsync(Project, relative, token).ConfigureAwait(false);
            }
            catch (StudioXException ex) when (ex.Code is "EDITOR_ENCODING" or "EDITOR_BINARY")
            {
                continue;
            }
            var utf8Bytes = QmdUtf8.GetByteCount(source.Text);
            if (utf8Bytes > QmdMaxFileBytes)
            {
                skippedOversize++;
                continue;
            }
            if (bytes + utf8Bytes > QmdMaxSourceBytes)
            {
                throw new StudioXException("MCP_QMD_SCOPE",
                "QMD 检索范围超过 8 MiB UTF-8 源码，请用 directory 指定更小的目录。");
            }
            files++;
            bytes += utf8Bytes;
            // 沿用固定数字镜像名，保持旧快照映射兼容并避免路径碰撞。
            var mirrorName = files.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) +
                Path.GetExtension(relative).ToLowerInvariant();
            var target = Path.GetFullPath(Path.Combine(mirror, mirrorName));
            var insideMirror = Path.GetRelativePath(mirror, target);
            if (insideMirror.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(insideMirror))
            {
                throw new StudioXException("MCP_QMD_PATH", "QMD 快照文件越过专用目录。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, source.Text, QmdUtf8, token).ConfigureAwait(false);
            paths[mirrorName] = relative;
        }
        return new QmdSnapshot(files, bytes, skippedOversize, paths);
    }

    /// <summary>持久缓存不视为可信数据；映射、顺序及内容均核对后才能复用索引。</summary>
    private async Task<bool> ValidateQmdSnapshotAsync(QmdSnapshotState state, QmdSources candidates,
        string mirror, CancellationToken token)
    {
        if (state.Files is < 1 or > QmdMaxFiles || state.Paths.Count != state.Files ||
            state.Bytes is < 0 or > QmdMaxSourceBytes || state.SkippedOversize < 0)
        {
            return false;
        }
        if ((File.GetAttributes(mirror) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }
        var candidateOrder = candidates.Files.Select((file, index) => (file.Relative, index))
            .ToDictionary(item => item.Relative, item => item.index, StringComparer.Ordinal);
        var previousIndex = -1;
        var bytes = 0L;
        for (var number = 1; number <= state.Files; number++)
        {
            token.ThrowIfCancellationRequested();
            var prefix = number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var matching = state.Paths.Where(item =>
                item.Key.StartsWith(prefix + ".", StringComparison.Ordinal)).Take(2).ToArray();
            if (matching.Length != 1)
            {
                return false;
            }
            var entry = matching[0];
            if (string.IsNullOrEmpty(entry.Value) ||
                !candidateOrder.TryGetValue(entry.Value, out var index) ||
                index <= previousIndex || !IsWorkspaceSourceFile(entry.Value) ||
                entry.Key != prefix + Path.GetExtension(entry.Value).ToLowerInvariant())
            {
                return false;
            }
            previousIndex = index;
            var mirrorPath = Path.GetFullPath(Path.Combine(mirror, entry.Key));
            if (!Path.GetDirectoryName(mirrorPath)!.Equals(Path.GetFullPath(mirror),
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(mirrorPath) ||
                (File.GetAttributes(mirrorPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            try
            {
                var source = await Services.Files.ReadAsync(Project, entry.Value, token)
                    .ConfigureAwait(false);
                var copy = await File.ReadAllTextAsync(mirrorPath, QmdUtf8, token)
                    .ConfigureAwait(false);
                if (!source.Text.Equals(copy, StringComparison.Ordinal))
                {
                    return false;
                }
                bytes += QmdUtf8.GetByteCount(copy);
                if (bytes > QmdMaxSourceBytes)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is StudioXException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return bytes == state.Bytes;
    }

    private static void PruneQmdProjectIndexes(string qmdRoot, string current)
    {
        var managed = Directory.EnumerateDirectories(qmdRoot)
            .Where(path => Path.GetFileName(path) is { Length: 20 } name && name.All(Uri.IsHexDigit))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => new { Path = path, Marker = Path.Combine(path, "managed.marker") })
            .Where(item => File.Exists(item.Marker) &&
                File.ReadAllText(item.Marker, QmdUtf8) == QmdManagedMarker)
            .OrderByDescending(item => File.GetLastWriteTimeUtc(item.Marker))
            .ToArray();
        foreach (var item in managed.Skip(QmdRetainedProjects))
        {
            if (Path.GetFullPath(item.Path).Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DeleteManagedQmdDirectory(qmdRoot, item.Path);
        }
    }

    private static void DeleteManagedQmdDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent);
        var fullTarget = Path.GetFullPath(target);
        if (!Path.GetDirectoryName(fullTarget)!.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullTarget))
        {
            throw new StudioXException("MCP_QMD_PATH", "QMD 清理目标不在专用索引目录内。");
        }
        var pending = new Stack<string>();
        pending.Push(fullTarget);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("MCP_QMD_PATH", "QMD 索引含有链接目录，拒绝清理。");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("MCP_QMD_PATH", "QMD 索引含有链接文件，拒绝清理。");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
        Directory.Delete(fullTarget, recursive: true);
    }

    private static async Task<FileStream> AcquireQmdLockAsync(string indexRoot, CancellationToken token)
    {
        var lockPath = Path.Combine(indexRoot, "index.lock");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
    }

    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length) : 0L;

    private sealed record QmdSnapshot(int Files, long Bytes, int SkippedOversize,
        IReadOnlyDictionary<string, string> Paths);
    private sealed record QmdSourceFile(string Relative, long ModifiedTicks, long Length);
    private sealed record QmdSources(IReadOnlyList<QmdSourceFile> Files, string Fingerprint,
        int SkippedOversize);
    private sealed record QmdSnapshotState(string Directory, string Fingerprint, int Files,
        long Bytes, int SkippedOversize, Dictionary<string, string> Paths);
}

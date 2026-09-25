namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;

/// <summary>可选的 QMD BM25 检索只处理当前工程的安全源码快照，不调用嵌入或重排模型。</summary>
public sealed partial class StudioXMcpTools
{
    private const int QmdMaxFiles = 400;
    private const long QmdMaxSourceBytes = 8L * 1024 * 1024;
    private const long QmdMaxFileBytes = 512L * 1024;
    private const long QmdMaxIndexBytes = 48L * 1024 * 1024;
    private const long QmdMaxProjectBytes = 64L * 1024 * 1024;
    private const int QmdRetainedProjects = 3;
    private const int QmdMaxOutputChars = 16_000;
    private static readonly UTF8Encoding QmdUtf8 = new(false);
    private const string QmdManagedMarker = "StudioX QMD BM25 cache v1";

    [McpServerTool(Name = "project_qmd_search")]
    [Description("可选 QMD 本地 BM25 检索当前工程安全源码，返回少量相关片段。首次调用会建立有界本地索引；不使用向量模型、重排或联网。目录过大时指定更窄的 directory。此工具降低重复读取的 token，不直接提高 API 前缀缓存命中率。")]
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
            throw new StudioXException("MCP_QMD_QUERY", "QMD 检索词或结果数量无效。");
        if (directory.Length > 0) RequireWorkspaceSourceDirectory(directory);
        var selectedRoot = directory.Length == 0 ? Project : PathBoundary.Resolve(Project, directory);
        if (!Directory.Exists(selectedRoot))
            throw new StudioXException("MCP_QMD_DIRECTORY", "QMD 检索目录不存在。");

        var entry = FindQmdEntry();
        if (entry is null)
            throw new StudioXException("MCP_QMD_NOT_INSTALLED",
                "未找到可用的 QMD。请自行安装 @tobilu/qmd 和 Node.js 后重启 StudioX；也可以使用 project_search。StudioX 不会自动下载模型或索引整个工程。");

        var projectKey = Convert.ToHexString(SHA256.HashData(QmdUtf8.GetBytes(Project.ToUpperInvariant())))[..20];
        var qmdRoot = Path.GetFullPath(Path.Combine(Services.DataDirectory, "qmd-bm25"));
        Directory.CreateDirectory(qmdRoot);
        await using var globalGate = await AcquireQmdLockAsync(qmdRoot, cancellationToken).ConfigureAwait(false);
        var indexRoot = Path.GetFullPath(Path.Combine(qmdRoot, projectKey));
        if (!Directory.Exists(indexRoot)) Directory.CreateDirectory(indexRoot);
        else
        {
            var marker = Path.Combine(indexRoot, "managed.marker");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 ||
                (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(marker, QmdUtf8) != QmdManagedMarker)
                throw new StudioXException("MCP_QMD_INDEX_PATH", "QMD 专用索引目录存在非 StudioX 数据，拒绝覆盖。");
        }
        await File.WriteAllTextAsync(Path.Combine(indexRoot, "managed.marker"), QmdManagedMarker,
            QmdUtf8, cancellationToken).ConfigureAwait(false);
        PruneQmdProjectIndexes(qmdRoot, indexRoot);
        await using var gate = await AcquireQmdLockAsync(indexRoot, cancellationToken).ConfigureAwait(false);
        var mirror = Path.Combine(indexRoot, "source");
        var config = Path.Combine(indexRoot, "config");
        var cache = Path.Combine(indexRoot, "cache");
        Directory.CreateDirectory(config);
        if (Directory.Exists(cache) && DirectorySize(cache) > QmdMaxIndexBytes)
            DeleteManagedQmdDirectory(indexRoot, cache);
        Directory.CreateDirectory(cache);

        var candidates = CollectQmdSources(selectedRoot, directory, cancellationToken);
        var statePath = Path.Combine(indexRoot, "snapshot.json");
        QmdSnapshotState? priorState = null;
        if (File.Exists(statePath) &&
            (File.GetAttributes(statePath) & FileAttributes.ReparsePoint) == 0 &&
            new FileInfo(statePath).Length <= 256 * 1024)
        {
            try { priorState = JsonSerializer.Deserialize<QmdSnapshotState>(
                await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false)); }
            catch (JsonException) { /* 状态损坏时重新建索引。 */ }
        }
        var reusable = priorState?.Paths is not null && priorState.Directory == directory &&
            priorState.Fingerprint == candidates.Fingerprint && Directory.Exists(mirror) &&
            File.Exists(Path.Combine(cache, "qmd", "studiox.sqlite")) &&
            await ValidateQmdSnapshotAsync(priorState, candidates, mirror, cancellationToken)
                .ConfigureAwait(false);
        QmdSnapshot snapshot;
        if (reusable)
            snapshot = new QmdSnapshot(priorState!.Files, priorState.Bytes,
                priorState.SkippedOversize, priorState.Paths);
        else
        {
            // 仅在目录或文件变化时重建；已删文件不会继续命中。
            if (File.Exists(statePath)) File.Delete(statePath);
            if (Directory.Exists(mirror)) DeleteManagedQmdDirectory(indexRoot, mirror);
            Directory.CreateDirectory(mirror);
            snapshot = await CreateQmdSnapshotAsync(candidates, mirror, cancellationToken)
                .ConfigureAwait(false);
        }
        if (snapshot.Files == 0)
            return JsonSerializer.Serialize(new { query, directory, indexedFiles = 0,
                skippedOversizeFiles = snapshot.SkippedOversize, results = Array.Empty<object>(),
                note = "所选目录没有可索引的安全源码；可使用 project_search 或缩小目录。" });

        if (!reusable)
        {
            var indexConfig = JsonSerializer.Serialize(new
            {
                collections = new { project = new { path = mirror, pattern = "**/*" } }
            });
            await File.WriteAllTextAsync(Path.Combine(config, "studiox.yml"), indexConfig, QmdUtf8,
                cancellationToken).ConfigureAwait(false);
            await RunQmdAsync(entry, indexRoot, config, cache,
                ["--index", "studiox", "update"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(statePath,
                JsonSerializer.Serialize(new QmdSnapshotState(directory, candidates.Fingerprint,
                    snapshot.Files, snapshot.Bytes, snapshot.SkippedOversize,
                    new Dictionary<string, string>(snapshot.Paths, StringComparer.Ordinal))), QmdUtf8,
                cancellationToken).ConfigureAwait(false);
        }
        if (DirectorySize(cache) > QmdMaxIndexBytes || DirectorySize(indexRoot) > QmdMaxProjectBytes)
        {
            DeleteManagedQmdDirectory(indexRoot, cache);
            throw new StudioXException("MCP_QMD_INDEX_SIZE",
                "QMD 索引超过 48 MiB（项目缓存上限 64 MiB），已清除超限索引；请缩小检索目录。");
        }
        var output = await RunQmdAsync(entry, indexRoot, config, cache,
            ["--index", "studiox", "search", query, "--format", "json", "-n",
                maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", "project"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return FormatQmdResults(output, query, directory, snapshot, reusable);
    }

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
                    continue;
                var relative = Path.GetRelativePath(Project, path).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsWorkspaceSourceDirectory(relative)) pending.Push(path);
                    continue;
                }
                if (!IsWorkspaceSourceFile(relative)) continue;
                var length = new FileInfo(path).Length;
                if (length > QmdMaxFileBytes) { skippedOversize++; continue; }
                if (files.Count + 1 > QmdMaxFiles || bytes + length > QmdMaxSourceBytes)
                    throw new StudioXException("MCP_QMD_SCOPE",
                        "QMD 检索范围超过 400 个文件或 8 MiB 源码，请用 directory 指定更小的目录；未创建大索引。");
                bytes += length;
                files.Add(new QmdSourceFile(relative, File.GetLastWriteTimeUtc(path).Ticks, length));
            }
        }
        files.Sort((left, right) => string.Compare(left.Relative, right.Relative, StringComparison.Ordinal));
        var signature = new StringBuilder(directory).Append('\n');
        foreach (var file in files)
            signature.Append(file.Relative).Append('|').Append(file.ModifiedTicks).Append('|')
                .Append(file.Length).Append('\n');
        var fingerprint = Convert.ToHexString(SHA256.HashData(QmdUtf8.GetBytes(signature.ToString())));
        return new QmdSources(files, fingerprint, skippedOversize);
    }

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
                try { source = await Services.Files.ReadAsync(Project, relative, token).ConfigureAwait(false); }
                catch (StudioXException ex) when (ex.Code is "EDITOR_ENCODING" or "EDITOR_BINARY")
                {
                    continue;
                }
                var utf8Bytes = QmdUtf8.GetByteCount(source.Text);
                if (utf8Bytes > QmdMaxFileBytes) { skippedOversize++; continue; }
                if (bytes + utf8Bytes > QmdMaxSourceBytes)
                    throw new StudioXException("MCP_QMD_SCOPE",
                        "QMD 检索范围超过 8 MiB UTF-8 源码，请用 directory 指定更小的目录。");
                files++;
                bytes += utf8Bytes;
                // QMD 会把下划线等字符规范化为连字符；固定数字镜像名避免路径碰撞。
                var mirrorName = files.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) +
                    Path.GetExtension(relative).ToLowerInvariant();
                var target = Path.GetFullPath(Path.Combine(mirror, mirrorName));
                var insideMirror = Path.GetRelativePath(mirror, target);
                if (insideMirror.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(insideMirror))
                    throw new StudioXException("MCP_QMD_PATH", "QMD 快照文件越过专用目录。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, source.Text, QmdUtf8, token).ConfigureAwait(false);
                paths[mirrorName] = relative;
        }
        return new QmdSnapshot(files, bytes, skippedOversize, paths);
    }

    private async Task<bool> ValidateQmdSnapshotAsync(QmdSnapshotState state, QmdSources candidates,
        string mirror, CancellationToken token)
    {
        if (state.Files is < 1 or > QmdMaxFiles || state.Paths.Count != state.Files ||
            state.Bytes is < 0 or > QmdMaxSourceBytes || state.SkippedOversize < 0)
            return false;
        if ((File.GetAttributes(mirror) & FileAttributes.ReparsePoint) != 0) return false;
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
            if (matching.Length != 1) return false;
            var entry = matching[0];
            if (string.IsNullOrEmpty(entry.Value) ||
                !candidateOrder.TryGetValue(entry.Value, out var index) ||
                index <= previousIndex || !IsWorkspaceSourceFile(entry.Value) ||
                entry.Key != prefix + Path.GetExtension(entry.Value).ToLowerInvariant())
                return false;
            previousIndex = index;
            var mirrorPath = Path.GetFullPath(Path.Combine(mirror, entry.Key));
            if (!Path.GetDirectoryName(mirrorPath)!.Equals(Path.GetFullPath(mirror),
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(mirrorPath) ||
                (File.GetAttributes(mirrorPath) & FileAttributes.ReparsePoint) != 0)
                return false;
            try
            {
                var source = await Services.Files.ReadAsync(Project, entry.Value, token)
                    .ConfigureAwait(false);
                var copy = await File.ReadAllTextAsync(mirrorPath, QmdUtf8, token)
                    .ConfigureAwait(false);
                if (!source.Text.Equals(copy, StringComparison.Ordinal)) return false;
                bytes += QmdUtf8.GetByteCount(copy);
                if (bytes > QmdMaxSourceBytes) return false;
            }
            catch (Exception ex) when (ex is StudioXException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return bytes == state.Bytes;
    }

    private static string FormatQmdResults(string output, string query, string directory,
        QmdSnapshot snapshot, bool reusedIndex)
    {
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new StudioXException("MCP_QMD_OUTPUT", "QMD 没有返回 JSON 结果数组。");
        var results = new List<object>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
                continue;
            var uri = file.GetString() ?? "";
            const string prefix = "qmd://project/";
            if (!uri.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var mirrorName = uri[prefix.Length..].Split('?', 2)[0];
            if (!snapshot.Paths.TryGetValue(mirrorName, out var path)) continue;
            if (!IsWorkspaceSourceFile(path)) continue;
            var snippet = item.TryGetProperty("snippet", out var snippetElement) &&
                snippetElement.ValueKind == JsonValueKind.String ? snippetElement.GetString() ?? "" : "";
            results.Add(new
            {
                path,
                line = item.TryGetProperty("line", out var line) && line.TryGetInt32(out var number) ? number : 1,
                score = item.TryGetProperty("score", out var score) && score.TryGetDouble(out var value)
                    ? value : 0d,
                snippet = snippet.Length > 900 ? snippet[..900] : snippet
            });
        }
        var serialized = JsonSerializer.Serialize(new { query, directory,
            indexedFiles = snapshot.Files, indexedSourceBytes = snapshot.Bytes,
            skippedOversizeFiles = snapshot.SkippedOversize, reusedIndex, results });
        if (serialized.Length > QmdMaxOutputChars)
            throw new StudioXException("MCP_QMD_OUTPUT", "QMD 结果异常过长，请减少 maxResults。");
        return serialized;
    }

    private static string? FindQmdEntry()
    {
        var roots = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length > 0) roots.Add(Path.Combine(appData, "npm"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        roots.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = Path.Combine(root, "node_modules", "@tobilu", "qmd", "bin", "qmd");
            if (File.Exists(entry)) return entry;
        }
        return null;
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
            if (Path.GetFullPath(item.Path).Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            DeleteManagedQmdDirectory(qmdRoot, item.Path);
        }
    }

    private static void DeleteManagedQmdDirectory(string parent, string target)
    {
        var fullParent = Path.GetFullPath(parent);
        var fullTarget = Path.GetFullPath(target);
        if (!Path.GetDirectoryName(fullTarget)!.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullTarget))
            throw new StudioXException("MCP_QMD_PATH", "QMD 清理目标不在专用索引目录内。");
        var pending = new Stack<string>();
        pending.Push(fullTarget);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("MCP_QMD_PATH", "QMD 索引含有链接目录，拒绝清理。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new StudioXException("MCP_QMD_PATH", "QMD 索引含有链接文件，拒绝清理。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
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
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> RunQmdAsync(string entry, string indexRoot, string config,
        string cache, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = indexRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(entry);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["QMD_CONFIG_DIR"] = config;
        start.Environment["XDG_CACHE_HOME"] = cache;
        start.Environment["QMD_FORCE_CPU"] = "1";
        Process? launched;
        try { launched = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new StudioXException("MCP_QMD_LAUNCH", "无法启动 Node.js：" + ex.Message);
        }
        using var process = launched ??
            throw new StudioXException("MCP_QMD_LAUNCH", "无法启动本机 QMD；请检查 Node.js 安装。");
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            var wait = process.WaitForExitAsync(limit.Token);
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(100)).ConfigureAwait(false);
                if (wait.IsCompleted) break;
                long size;
                try { size = DirectorySize(indexRoot); }
                catch (IOException) { continue; }
                if (size <= QmdMaxProjectBytes) continue;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (Directory.Exists(cache)) DeleteManagedQmdDirectory(indexRoot, cache);
                throw new StudioXException("MCP_QMD_INDEX_SIZE",
                    "QMD 项目缓存超过 64 MiB，已停止索引并清除缓存；请缩小目录。");
            }
            await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new StudioXException("MCP_QMD_TIMEOUT", "QMD 检索或索引超时；请缩小目录。");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new StudioXException("MCP_QMD_FAILED",
                "QMD 执行失败：" + LimitOutput(error.Length > 0 ? error : output, 2_000));
        return output;
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

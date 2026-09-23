namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record ToolsetManifest(int FormatVersion, string Id, string Version, string Host, string CompilerId,
    Dictionary<string, string> Executables, Dictionary<string, string> Sha256, string? DisplayName = null,
    Dictionary<string, string>? ComponentVersions = null, Dictionary<string, string>? ResourceDirectories = null);
public sealed record ResolvedToolset(ToolsetManifest Manifest, string RootDirectory, string Fingerprint)
{
    public string Tool(string role) => PathBoundary.Resolve(RootDirectory, Manifest.Executables.TryGetValue(role, out var path)
        ? path : throw new StudioXException("TOOL_ROLE", $"工具集缺少组件：{role}"));
    public string ResourceDirectory(string role) => PathBoundary.Resolve(RootDirectory, Manifest.ResourceDirectories is not null && Manifest.ResourceDirectories.TryGetValue(role, out var path)
        ? path : throw new StudioXException("TOOL_RESOURCE", $"工具集缺少资源目录：{role}"));
}

public sealed class ToolsetCatalog(string rootDirectory)
{
    private readonly SemaphoreSlim verificationGate = new(1, 1);
    private readonly Dictionary<string, VerifiedTree> verified = new(StringComparer.OrdinalIgnoreCase);
    private sealed record VerifiedTree(string Fingerprint, ToolsetSnapshot Snapshot);
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    // 目录枚举、JSON 解析和路径检查包含同步工作；即使异步 I/O 命中缓存也不能占用调用方的 UI 线程。
    public Task<ResolvedToolset> ResolveAsync(string id, string version, string compilerId, CancellationToken cancellationToken = default,
        bool forceVerification = false, IProgress<string>? progress = null)
        => Task.Run(() => ResolveInBackgroundAsync(id, version, compilerId, cancellationToken, forceVerification, progress), cancellationToken);
    private async Task<ResolvedToolset> ResolveInBackgroundAsync(string id, string version, string compilerId, CancellationToken cancellationToken,
        bool forceVerification, IProgress<string>? progress)
    {
        PackValidator.Token(id); PackValidator.Version(version);
        var root = PathBoundary.Resolve(RootDirectory, $"{id}/{version}");
        await verificationGate.WaitAsync(cancellationToken);
        try { return await ResolveCoreAsync(root, id, version, compilerId, forceVerification, progress, cancellationToken); }
        catch { verified.Remove(root); throw; }
        finally { verificationGate.Release(); }
    }
    private async Task<ResolvedToolset> ResolveCoreAsync(string root, string id, string version, string compilerId,
        bool forceVerification, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "toolset.json");
        if (!File.Exists(manifestPath)) throw new StudioXException("TOOLSET_MISSING", $"内置工具集缺失：{id} {version}。请安装包含该工具集的 StudioX 发行版。");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var fingerprint = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var jsonOffset = manifestBytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<ToolsetManifest>(manifestBytes.AsSpan(jsonOffset), JsonStore.Options)
            ?? throw new StudioXException("TOOLSET_MANIFEST", "工具清单没有有效内容。");
        if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.Version != version || manifest.Host != "win-x64" || manifest.CompilerId != compilerId)
            throw new StudioXException("TOOLSET_INCOMPATIBLE", "工具集版本、宿主或编译器与器件要求不一致。");
        if (manifest.Executables is null || manifest.Sha256 is null) throw new StudioXException("TOOLSET_MANIFEST", "工具清单不完整。");
        if (manifest.ResourceDirectories is not null)
            foreach (var relative in manifest.ResourceDirectories.Values)
                if (!Directory.Exists(PathBoundary.Resolve(root, relative))) throw new StudioXException("TOOL_RESOURCE", "工具资源目录缺失：" + relative);
        var requiredRoles = id == "stc.sdcc"
            ? new[] { "cmake", "ninja", "sdcc", "sdar", "sdas8051", "sdld", "packihx" }
            : new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" };
        foreach (var role in requiredRoles)
        {
            if (!manifest.Executables.TryGetValue(role, out var relative)) throw new StudioXException("TOOL_ROLE", $"工具集缺少 {role}。");
            var executable = PathBoundary.Resolve(root, relative);
            if (!File.Exists(executable)) throw new StudioXException("TOOL_MISSING", $"内置工具文件缺失：{relative}");
        }
        var snapshot = ToolsetSnapshot.Capture(root, cancellationToken);
        foreach (var relative in manifest.Executables.Values)
            if (!manifest.Sha256.ContainsKey(relative)) throw new StudioXException("TOOL_HASH", $"工具入口未纳入校验：{relative}");
        var actualFiles = snapshot.Files.Where(p => p != "toolset.json").ToHashSet(StringComparer.Ordinal);
        // GDB 内嵌 Python 可能在发行后的 share/gdb/python 下生成字节码。只容许有已索引 .py
        // 源文件对应的标准 CPython 缓存；任意其他新增文件仍视为工具集被改动。
        var unexpected = actualFiles.Except(manifest.Sha256.Keys, StringComparer.Ordinal)
            .Where(path => !IsGdbPythonCache(path, manifest.Sha256))
            .ToArray();
        if (unexpected.Length != 0 || manifest.Sha256.Keys.Any(path => !actualFiles.Contains(path)))
            throw new StudioXException("TOOL_HASH", "工具目录与发行索引不一致。");
        var indexedCount = manifest.Sha256.Count;
        if (!forceVerification && verified.TryGetValue(root, out var previous) && previous.Fingerprint == fingerprint && previous.Snapshot.Matches(snapshot))
        {
            progress?.Report($"工具集未变化，复用本次启动的校验结果（{indexedCount:N0} 个文件）。");
            return new ResolvedToolset(manifest, root, fingerprint);
        }
        // 只复用本进程内成功校验的结果；重启、文件/目录元数据变化或手动检查均重新完整哈希。
        // 取消/失败不能留下可复用结果，仍覆盖 DLL、库、头文件和 OpenOCD 脚本，而不只是入口 exe。
        verified.Remove(root);
        progress?.Report($"完整校验内置工具集（{indexedCount:N0} 个文件）…");
        var completed = 0;
        var lastProgress = Stopwatch.GetTimestamp();
        foreach (var (relative, expected) in manifest.Sha256)
        {
            await using var source = File.OpenRead(PathBoundary.Resolve(root, relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("TOOL_HASH", $"内置工具文件已损坏：{relative}");
            completed++;
            if (progress is not null && Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 250)
            {
                progress.Report($"校验内置工具集… {completed * 100 / indexedCount}%（{completed:N0}/{indexedCount:N0}）");
                lastProgress = Stopwatch.GetTimestamp();
            }
        }
        var after = ToolsetSnapshot.Capture(root, cancellationToken);
        var afterManifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        if (!snapshot.Matches(after) || !manifestBytes.AsSpan().SequenceEqual(afterManifest))
        {
            var changed = snapshot.FirstDifference(after) ?? "toolset.json";
            throw new StudioXException("TOOL_CHANGED", "校验期间工具目录发生变化（" + changed + "），请等待更新完成后重试。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        verified[root] = new(fingerprint, after);
        return new ResolvedToolset(manifest, root, fingerprint);
    }
    public IEnumerable<string> ManifestPaths() => Directory.Exists(RootDirectory)
        ? Directory.EnumerateFiles(RootDirectory, "toolset.json", SearchOption.AllDirectories) : [];

    private static bool IsGdbPythonCache(string path, IReadOnlyDictionary<string, string> indexed)
    {
        const string basePath = "gcc/share/gdb/python/";
        const string cacheSegment = "/__pycache__/";
        if (!path.StartsWith(basePath, StringComparison.Ordinal)) return false;
        var cacheStart = path.LastIndexOf(cacheSegment, StringComparison.Ordinal);
        if (cacheStart < basePath.Length - 1) return false;
        var fileName = path[(cacheStart + cacheSegment.Length)..];
        if (fileName.Contains('/')) return false;
        var match = Regex.Match(fileName, @"^([A-Za-z_][A-Za-z0-9_]*)\.cpython-[0-9]+(?:\.opt-[0-9]+)?\.pyc$", RegexOptions.CultureInvariant);
        return match.Success && indexed.ContainsKey(path[..cacheStart] + "/" + match.Groups[1].Value + ".py");
    }
}

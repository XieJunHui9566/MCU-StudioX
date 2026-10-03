namespace StudioX.Engine;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public sealed class ToolsetCatalog
{
    private readonly ToolComponentActivation? activation;
    public ToolsetCatalog(string rootDirectory, string? userDataDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        if (userDataDirectory is not null)
        {
            activation = new(userDataDirectory);
            ToolUsageLease.RegisterUseGuard(RootDirectory, root =>
                activation.IsEnabled(Path.GetFileName(Path.GetDirectoryName(root))!, Path.GetFileName(root)));
        }
    }
    private readonly SemaphoreSlim verificationGate = new(1, 1);
    private readonly Dictionary<string, VerifiedTree> verified = new(StringComparer.OrdinalIgnoreCase);
    private sealed record VerifiedTree(string Fingerprint, ToolsetSnapshot Snapshot);
    public string RootDirectory { get; }
    public bool IsEnabled(string id, string version) => activation?.IsEnabled(id, version) ?? true;
    public void RequireEnabled(string id, string version)
    {
        if (!IsEnabled(id, version)) throw new StudioXException("TOOLSET_DISABLED", $"开发环境组件 {id} / {version} 已禁用，请在组件管理中启用。工程不会改用其他版本。");
    }
    public async Task SetEnabledAsync(string id, string version, bool enabled, CancellationToken token = default)
    {
        PackValidator.Token(id); PackValidator.Version(version);
        if (activation is null) throw new StudioXException("TOOLS_ACTIVATION", "组件启用管理需要独立用户数据目录。");
        using var lease = ToolUsageLease.Acquire(PathBoundary.Resolve(RootDirectory, id + "/" + version), maintenance: true);
        await activation.SetEnabledAsync(id, version, enabled, token);
    }
    // 目录枚举、JSON 解析和路径检查包含同步工作；即使异步 I/O 命中缓存也不能占用调用方的 UI 线程。
    public Task<ResolvedToolset> ResolveAsync(string id, string version, string compilerId, CancellationToken cancellationToken = default,
        bool forceVerification = false, IProgress<string>? progress = null, bool allowDisabled = false)
        => Task.Run(() => ResolveInBackgroundAsync(id, version, compilerId, cancellationToken, forceVerification, progress, allowDisabled), cancellationToken);
    private async Task<ResolvedToolset> ResolveInBackgroundAsync(string id, string version, string compilerId, CancellationToken cancellationToken,
        bool forceVerification, IProgress<string>? progress, bool allowDisabled)
    {
        PackValidator.Token(id);
        PackValidator.Version(version);
        var root = PathBoundary.Resolve(RootDirectory, $"{id}/{version}");
        using var toolLease = ToolUsageLease.Acquire(root, ignoreActivation: allowDisabled);
        if (!allowDisabled) RequireEnabled(id, version);
        await verificationGate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveCoreAsync(root, id, version, compilerId, forceVerification, progress, cancellationToken);
        }
        catch { verified.Remove(root); throw; }
        finally { verificationGate.Release(); }
    }
    private async Task<ResolvedToolset> ResolveCoreAsync(string root, string id, string version, string compilerId,
        bool forceVerification, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "toolset.json");
        if (!File.Exists(manifestPath))
        {
            throw new StudioXException("TOOLSET_MISSING", $"缺少开发环境组件：{id} {version}。请通过“准备工程开发环境组件”下载，或导入对应版本的 .mcutoolchain 文件。");
        }
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        var fingerprint = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var jsonOffset = manifestBytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<ToolsetManifest>(manifestBytes.AsSpan(jsonOffset), JsonStore.Options)
            ?? throw new StudioXException("TOOLSET_MANIFEST", "工具清单没有有效内容。");
        if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.Version != version || manifest.Host != "win-x64" || manifest.CompilerId != compilerId)
        {
            throw new StudioXException("TOOLSET_INCOMPATIBLE", "开发环境组件版本、宿主或编译器与器件要求不一致。");
        }
        if (manifest.Executables is null || manifest.Sha256 is null)
        {
            throw new StudioXException("TOOLSET_MANIFEST", "工具清单不完整。");
        }
        if (manifest.Purpose is not (null or "windows-native" or "esp-idf" or "esp8266-rtos-sdk" or "ag32-mapping" or "hdl-native" or "hdl-simulation"))
        {
            throw new StudioXException("TOOLSET_PURPOSE", "工具清单包含不支持的用途。");
        }
        if (manifest.ResourceDirectories is not null)
        {
            foreach (var relative in manifest.ResourceDirectories.Values)
            {
                if (!Directory.Exists(PathBoundary.Resolve(root, relative)))
                {
                    throw new StudioXException("TOOL_RESOURCE", "工具资源目录缺失：" + relative);
                }
            }
        }
        var requiredRoles = manifest.Purpose == "ag32-mapping"
            ? new[] { "python", "converter", "supra" }
            : manifest.Purpose == "hdl-native" ? new[] { "mapper" }
            : manifest.Purpose == "hdl-simulation" ? new[] { "iverilog", "vvp" }
            : manifest.Purpose is "esp-idf" or "esp8266-rtos-sdk"
            ? EspressifToolsetRequirements.RequiredRoles(manifest.Purpose)
            : manifest.Purpose == "windows-native"
            ? new[] { "gcc", "gxx", "ar", "ranlib", "as", "ld", "objcopy", "objdump", "size" }
            : id == "stc.sdcc"
            ? new[] { "cmake", "ninja", "sdcc", "sdar", "sdas8051", "sdld", "packihx" }
            : new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" };
        if (manifest.Purpose is "esp-idf" or "esp8266-rtos-sdk")
        {
            EspressifToolsetRequirements.ValidateResources(manifest, root);
        }
        foreach (var role in requiredRoles)
        {
            if (!manifest.Executables.TryGetValue(role, out var relative))
            {
                throw new StudioXException("TOOL_ROLE", $"开发环境组件缺少 {role}。");
            }
            var executable = PathBoundary.Resolve(root, relative);
            if (!File.Exists(executable))
            {
                throw new StudioXException("TOOL_MISSING", $"内置工具文件缺失：{relative}");
            }
        }
        var snapshot = ToolsetSnapshot.Capture(root, cancellationToken);
        foreach (var relative in manifest.Executables.Values)
        {
            if (!manifest.Sha256.ContainsKey(relative))
            {
                throw new StudioXException("TOOL_HASH", $"工具入口未纳入校验：{relative}");
            }
        }
        var actualFiles = snapshot.Files.Where(p => p != "toolset.json").ToHashSet(StringComparer.Ordinal);
        // GDB 内嵌 Python 可能在发行后的 share/gdb/python 下生成字节码。只容许有已索引 .py
        // 源文件对应的标准 CPython 缓存；任意其他新增文件仍视为开发环境组件被改动。
        var unexpected = actualFiles.Except(manifest.Sha256.Keys, StringComparer.Ordinal)
            .Where(path => !IsGdbPythonCache(path, manifest.Sha256))
            .ToArray();
        if (unexpected.Length != 0 || manifest.Sha256.Keys.Any(path => !actualFiles.Contains(path)))
        {
            throw new StudioXException("TOOL_HASH", "工具目录与发行索引不一致。");
        }
        var indexedCount = manifest.Sha256.Count;
        if (!forceVerification && verified.TryGetValue(root, out var previous) && previous.Fingerprint == fingerprint && previous.Snapshot.Matches(snapshot))
        {
            progress?.Report($"开发环境组件 {id} / {version} 未变化，复用本次启动的校验结果（{indexedCount:N0} 个文件）。");
            return new ResolvedToolset(manifest, root, fingerprint);
        }
        // 只复用本进程内成功校验的结果；重启、文件/目录元数据变化或手动检查均重新完整哈希。
        // 取消/失败不能留下可复用结果，仍覆盖 DLL、库、头文件和 OpenOCD 脚本，而不只是入口 exe。
        verified.Remove(root);
        progress?.Report($"完整校验开发环境组件 {id} / {version}（{indexedCount:N0} 个文件）…");
        var completed = 0;
        var lastProgress = Stopwatch.GetTimestamp();
        foreach (var (relative, expected) in manifest.Sha256)
        {
            await using var source = File.OpenRead(PathBoundary.Resolve(root, relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("TOOL_HASH", $"内置工具文件已损坏：{relative}");
            }
            completed++;
            if (progress is not null && Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 250)
            {
                progress.Report($"校验开发环境组件 {id} / {version}… {completed * 100 / indexedCount}%（{completed:N0}/{indexedCount:N0}）");
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
    public IEnumerable<string> ManifestPaths()
    {
        if (!Directory.Exists(RootDirectory))
        {
            yield break;
        }
        // 只枚举约定的 ID/版本层级；备份、修复临时目录和 SDK 内的同名文件不是已安装开发环境组件。
        foreach (var id in Directory.EnumerateDirectories(RootDirectory).Where(p => !Path.GetFileName(p).StartsWith('.') && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0))
        {
            foreach (var version in Directory.EnumerateDirectories(id).Where(p => !Path.GetFileName(p).StartsWith('.') && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0))
            {
                var manifest = Path.Combine(version, "toolset.json");
                if (File.Exists(manifest))
                {
                    yield return manifest;
                }
            }
        }
    }

    private static bool IsGdbPythonCache(string path, IReadOnlyDictionary<string, string> indexed)
    {
        const string basePath = "gcc/share/gdb/python/";
        const string cacheSegment = "/__pycache__/";
        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return false;
        }
        var cacheStart = path.LastIndexOf(cacheSegment, StringComparison.Ordinal);
        if (cacheStart < basePath.Length - 1)
        {
            return false;
        }
        var fileName = path[(cacheStart + cacheSegment.Length)..];
        if (fileName.Contains('/'))
        {
            return false;
        }
        var match = Regex.Match(fileName, @"^([A-Za-z_][A-Za-z0-9_]*)\.cpython-[0-9]+(?:\.opt-[0-9]+)?\.pyc$", RegexOptions.CultureInvariant);
        return match.Success && indexed.ContainsKey(path[..cacheStart] + "/" + match.Groups[1].Value + ".py");
    }
}

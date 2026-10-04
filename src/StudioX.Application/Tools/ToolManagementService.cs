namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>开发环境组件版本并存、依赖保留与可恢复清理；不迁移工程锁或调用工具程序。</summary>
public sealed partial class ToolManagementService(ToolsetCatalog catalog, PackRepository packs, RecentProjectService recent,
    string dataDirectory, Func<bool>? sessionActive = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string RegistryPath => Path.Combine(dataDirectory, "tool-management-projects.json");
    public async Task RegisterProjectAsync(string project, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        _ = await ProjectService.ReadAsync(root, token);
        await gate.WaitAsync(token);
        try
        {
            var known = await RegisteredAsync(token);
            await JsonStore.WriteAsync(RegistryPath, known.Append(root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), token);
        }
        finally { gate.Release(); }
    }
    public async Task ForgetProjectAsync(string project, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await JsonStore.WriteAsync(RegistryPath, (await RegisteredAsync(token)).Where(path => !path.Equals(project, StringComparison.OrdinalIgnoreCase)).ToArray(), token);
        }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<string>> RegisteredAsync(CancellationToken token = default) => File.Exists(RegistryPath)
        ? ReadRegistryAsync(token) : Task.FromResult<IReadOnlyList<string>>([]);
    private async Task<IReadOnlyList<string>> ReadRegistryAsync(CancellationToken token) => await JsonStore.ReadAsync<string[]>(RegistryPath, token);
    public Task<ToolManagementReport> InspectAsync(string? currentProject, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(() => InspectCoreAsync(currentProject, progress, token), token);
    private async Task<ToolManagementReport> InspectCoreAsync(string? currentProject, IProgress<string>? progress, CancellationToken token)
    {
        var diagnostics = new List<string>();
        var recovery = await ToolRepairTransactions.InspectAsync(catalog.RootDirectory, token);
        diagnostics.AddRange(recovery.Diagnostics);
        var references = await ReadReferencesAsync(currentProject, diagnostics, token);
        var versions = new List<ManagedToolVersion>();
        foreach (var path in catalog.ManifestPaths().Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("统计工具目录：" + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) + "…");
            versions.Add(await DescribeVersionAsync(path, null, references.Items, diagnostics, token));
        }
        var retired = PathBoundary.Resolve(catalog.RootDirectory, ".retired");
        if (Directory.Exists(retired))
        {
            foreach (var directory in Directory.EnumerateDirectories(retired))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var key = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(key, "N", out _))
                    {
                        throw new StudioXException("TOOLS_RETIREMENT", "可恢复目录身份无效：" + key);
                    }
                    var record = await JsonStore.ReadAsync<ToolRetirement>(PathBoundary.Resolve(retired, key + "/retirement.json"), token);
                    if (record.State == "restored")
                    {
                        continue;
                    }
                    PackValidator.Token(record.Id);
                    PackValidator.Version(record.Version);
                    if (record.FormatVersion != 1 || record.State is not ("retired" or "purging"))
                    {
                        throw new StudioXException("TOOLS_RETIREMENT", "不支持的恢复记录。");
                    }
                    if (record.State == "purging")
                    {
                        var partialRoot = PathBoundary.Resolve(directory, record.Id + "/" + record.Version);
                        var tree = Directory.Exists(partialRoot) ? CaptureTree(partialRoot, token) : (Bytes: 0L, Files: 0, Stamp: "empty");
                        versions.Add(new(record.Id, record.Version, record.Id, record.CompilerId, tree.Bytes, tree.Files, false, key, false,
                            ToolUsageLease.IsBusy(partialRoot), true, record.Fingerprint, tree.Stamp,
                            references.Items.GetValueOrDefault(record.Id + "/" + record.Version)?.Distinct().ToArray() ?? [], "", "永久删除曾中断，可重试删除剩余内容；此版本不能恢复。", "purging"));
                        continue;
                    }
                    var entry = await DescribeVersionAsync(PathBoundary.Resolve(directory, record.Id + "/" + record.Version + "/toolset.json"), key, references.Items, diagnostics, token);
                    if (entry.Id != record.Id || entry.Version != record.Version || entry.CompilerId != record.CompilerId || entry.Fingerprint != record.Fingerprint)
                    {
                        throw new StudioXException("TOOLS_RETIREMENT", "恢复记录与工具内容身份不一致。");
                    }
                    versions.Add(entry);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { diagnostics.Add(error.ToString()); }
            }
        }
        var latest = versions.Where(version => version.Installed && System.Version.TryParse(version.Version, out _)).GroupBy(version => version.Id)
            .ToDictionary(group => group.Key, group => group.Max(version => System.Version.Parse(version.Version))!);
        var complete = diagnostics.Count == 0;
        return new(DateTimeOffset.UtcNow, versions.Select(version => version with
        {
            Enabled = catalog.IsEnabled(version.Id, version.Version),
            Latest = version.Installed && latest.TryGetValue(version.Id, out var newest) && System.Version.TryParse(version.Version, out var number) && number == newest,
            SafeToManage = version.SafeToManage && complete && System.Version.TryParse(version.Version, out _)
                && !recovery.Items.Any(item => item.Id == version.Id && item.Version == version.Version)
        }).OrderBy(version => version.Id).ThenByDescending(version => System.Version.TryParse(version.Version, out var number) ? number : new System.Version(0, 0)).ToArray(),
            references.Projects, diagnostics, complete)
        {
            Recoveries = recovery.Items
        };
    }
    private async Task<ManagedToolVersion> DescribeVersionAsync(string path, string? retiredId,
        Dictionary<string, List<string>> references, List<string> diagnostics, CancellationToken token)
    {
        var version = Path.GetFileName(Path.GetDirectoryName(path))!;
        var id = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!;
        try
        {
            PackValidator.Token(id);
            PackValidator.Version(version);
            var bytes = await File.ReadAllBytesAsync(path, token);
            if (bytes.Length > 64 * 1024 * 1024)
            {
                throw new StudioXException("TOOLS_MANIFEST_SIZE", "工具清单过大。");
            }
            var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
                ?? throw new StudioXException("TOOLS_MANIFEST", "工具清单为空。");
            if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.Version != version || manifest.Host != "win-x64" || manifest.Sha256 is null || manifest.Executables is null)
            {
                throw new StudioXException("TOOLS_IDENTITY", "工具清单与目录身份不一致。");
            }
            var root = Path.GetDirectoryName(path)!;
            var tree = CaptureTree(root, token);
            return new(id, version, manifest.DisplayName ?? id, manifest.CompilerId, tree.Bytes, tree.Files, retiredId is null, retiredId, false,
                ToolUsageLease.IsBusy(root), true, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), tree.Stamp,
                references.GetValueOrDefault(id + "/" + version)?.Distinct().ToArray() ?? [], Components(manifest), "未执行哈希校验；空间含目录内全部文件。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            diagnostics.Add(error.ToString());
            return new(id, version, id, "", 0, 0, retiredId is null, retiredId, false, false, false, "", "", [], "", error.ToString());
        }
    }
    private static string Components(ToolsetManifest manifest) => manifest.ComponentVersions is null ? "未声明组件版本" : string.Join("；", manifest.ComponentVersions.Select(item => item.Key + " " + item.Value));
    private static (long Bytes, int Files, string Stamp) CaptureTree(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        long bytes = 0;
        var files = 0;
        var visited = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "工具目录包含链接：" + directory);
            }
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 300000)
                {
                    throw new StudioXException("TOOLS_TREE_SIZE", "工具目录条目过多，停止清理检查。");
                }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("PATH_LINK", "工具目录包含链接：" + entry.FullName);
                }
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (entry is DirectoryInfo child)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(relative + ":dir\n"));
                    pending.Push(child.FullName);
                }
                else if (entry is FileInfo file)
                {
                    bytes += file.Length;
                    files++;
                    hash.AppendData(Encoding.UTF8.GetBytes(relative + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.CreationTimeUtc.Ticks + ":" + (int)file.Attributes + "\n"));
                }
            }
        }
        return (bytes, files, Convert.ToHexString(hash.GetHashAndReset()));
    }
    private void EnsureIdle()
    {
        if (sessionActive?.Invoke() == true)
        {
            throw new StudioXException("TOOLS_SESSION_ACTIVE", "请先结束调试、预览或当前工具操作再管理版本。");
        }
    }
    private string VersionRoot(ManagedToolVersion version) => PathBoundary.Resolve(catalog.RootDirectory,
        (version.RetirementId is null ? "" : ".retired/" + version.RetirementId + "/") + version.Id + "/" + version.Version);
    public Task ExportReportAsync(ToolManagementReport report, string output, CancellationToken token = default) => JsonStore.WriteAsync(output, report, token);
}

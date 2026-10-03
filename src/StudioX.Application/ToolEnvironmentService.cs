namespace StudioX.Application;

using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>管理内置开发环境组件版本和离线归档，修复使用已校验副本并保留回滚目录。</summary>
public sealed class ToolEnvironmentService(ToolsetCatalog catalog)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<IReadOnlyList<ToolEnvironmentEntry>> InspectAsync(string? project, CancellationToken token = default) => Task.Run(async () =>
    {
        var result = new List<ToolEnvironmentEntry>();
        var manifest = project is null ? null : await ProjectService.ReadAsync(project, token).ConfigureAwait(false);
        var needs = manifest is null ? [] : await ProjectDevelopmentComponents.ReadAsync(project!, manifest, token).ConfigureAwait(false);
        foreach (var path in catalog.ManifestPaths())
        {
            token.ThrowIfCancellationRequested();
            var tools = await JsonStore.ReadAsync<ToolsetManifest>(path, token).ConfigureAwait(false);
            // 目录枚举已经携带大小和属性；逐个 Resolve 会重复检查每个 SDK 文件的所有祖先目录。
            var folder = PathBoundary.Resolve(catalog.RootDirectory, tools.Id + "/" + tools.Version);
            var indexed = tools.Sha256.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            var found = 0;
            var links = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new(folder));
            while (pending.TryPop(out var directory))
            {
                foreach (var info in directory.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        links++;
                        continue;
                    }
                    if (info is DirectoryInfo child)
                    {
                        pending.Push(child);
                    }
                    else if (info is FileInfo file && indexed.Contains(Path.GetRelativePath(folder, file.FullName).Replace('\\', '/')))
                    {
                        bytes += file.Length;
                        found++;
                    }
                }
            }
            var missing = indexed.Count - found;
            var required = needs.Any(item => item.Id == tools.Id && item.Version == tools.Version);
            result.Add(new(tools.Id, tools.Version, tools.DisplayName ?? tools.Id, tools.CompilerId, bytes, tools.Sha256.Count,
                required, links > 0 ? $"发现 {links} 个链接，需校验" : missing == 0 ? "未进行哈希校验" : $"缺少 {missing} 个文件", tools.ComponentVersions is null ? "" : string.Join("；", tools.ComponentVersions.Select(p => p.Key + " " + p.Value))));
        }
        foreach (var need in needs.Where(item => !result.Any(r => r.Id == item.Id && r.Version == item.Version)))
        {
            result.Insert(0, new(need.Id, need.Version, "工程需要的开发环境组件", need.CompilerId, 0, 0, true, "未安装"));
        }
        return (IReadOnlyList<ToolEnvironmentEntry>)result.OrderByDescending(r => r.Required).ThenBy(r => r.Id).ToArray();
    }, token);

    public async Task VerifyAsync(ToolEnvironmentEntry entry, IProgress<string>? progress, CancellationToken token = default)
        => _ = await catalog.ResolveAsync(entry.Id, entry.Version, entry.CompilerId, token, true, progress, allowDisabled: true).ConfigureAwait(false);

    public async Task ExportAsync(ToolEnvironmentEntry entry, string output, IProgress<string>? progress, CancellationToken token = default)
    {
        ToolchainArchiveFormat.ValidateFileName(output);
        var resolved = await catalog.ResolveAsync(entry.Id, entry.Version, entry.CompilerId, token, true, progress, allowDisabled: true).ConfigureAwait(false);
        using var toolLease = ToolUsageLease.Acquire(resolved.RootDirectory, ignoreActivation: true);
        var manifest = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(resolved.RootDirectory, "toolset.json"), token).ConfigureAwait(false);
        var temporary = Path.GetFullPath(output) + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await ToolchainArchiveWriter.WriteAsync(resolved.RootDirectory, manifest.Sha256.Keys.Order(StringComparer.Ordinal).Prepend("toolset.json"),
                temporary, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public Task<string> RepairAsync(ToolEnvironmentEntry entry, string archive, IProgress<string>? progress, CancellationToken token = default)
        => RepairAsync(entry, archive, progress, token, installOnly: false);
    public Task<string> RepairAsync(ToolEnvironmentEntry entry, string archive, IProgress<string>? progress, CancellationToken token,
        bool installOnly, string? expectedArchiveHash = null)
        => Task.Run(() => RepairCoreAsync(entry, archive, progress, token, installOnly, expectedArchiveHash), token);

    private async Task<string> RepairCoreAsync(ToolEnvironmentEntry entry, string archive, IProgress<string>? progress, CancellationToken token,
        bool installOnly, string? expectedArchiveHash)
    {
        ToolchainArchiveFormat.ValidateFileName(archive);
        await gate.WaitAsync(token).ConfigureAwait(false);
        var staging = Path.Combine(catalog.RootDirectory, ".repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var archiveContent = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (expectedArchiveHash is not null)
            {
                if (!Convert.ToHexString(await SHA256.HashDataAsync(archiveContent, token)).Equals(expectedArchiveHash, StringComparison.OrdinalIgnoreCase))
                    throw new StudioXException("TOOLS_CHANGED", "离线归档在预览后发生变化。");
            }
            archiveContent.Position = 0;
            using var container = ToolchainArchive.Open(archiveContent, token);
            var manifestBytes = await container.ReadManifestAsync(token).ConfigureAwait(false);
            var jsonOffset = manifestBytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ToolsetManifest>(manifestBytes.AsSpan(jsonOffset), JsonStore.Options)
                ?? throw new StudioXException("TOOLS_ARCHIVE", "工具清单为空。");
            if (manifest.Id != entry.Id || manifest.Version != entry.Version || manifest.CompilerId != entry.CompilerId)
            {
                throw new StudioXException("TOOLS_IDENTITY", "离线包与所选开发环境组件的 ID、版本或编译器不一致。");
            }
            PackValidator.Token(manifest.Id);
            PackValidator.Version(manifest.Version);
            _ = manifest.Identity;
            if (manifest.Sha256 is null || manifest.Executables is null) throw new StudioXException("TOOLS_IDENTITY", "组件清单不完整。");
            container.ValidateIndex(manifest.Sha256);
            var relativeRoot = entry.Id + "/" + entry.Version;
            var extracted = PathBoundary.Resolve(staging, relativeRoot);
            Directory.CreateDirectory(extracted);
            await container.ReadFilesAsync(async (file, source) =>
            {
                token.ThrowIfCancellationRequested();
                var destination = PathBoundary.Resolve(extracted, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                progress?.Report("校验副本：" + file.Name);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                await ToolchainArchive.CopyExactAsync(source, output, file.Length, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            _ = await new ToolsetCatalog(staging).ResolveAsync(entry.Id, entry.Version, entry.CompilerId, token, true, progress, allowDisabled: true).ConfigureAwait(false);
            var target = PathBoundary.Resolve(catalog.RootDirectory, relativeRoot);
            using var toolLease = ToolUsageLease.Acquire(target, maintenance: true);
            if (installOnly && (Directory.Exists(target) || File.Exists(target)))
                throw new StudioXException("TOOLS_VERSION_EXISTS", "同一开发环境组件版本已经存在；并存安装不覆盖任何已有内容。");
            var backup = PathBoundary.Resolve(catalog.RootDirectory, ".rollback-" + entry.Id + "-" + Guid.NewGuid().ToString("N"));
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var existed = Directory.Exists(target);
            if (existed)
            {
                Directory.Move(target, backup);
            }
            try
            {
                Directory.Move(extracted, target);
            }
            catch { if (existed) { Directory.Move(backup, target); } throw; }
            return existed ? backup : "";
        }
        finally
        {
            // staging 是本服务在工具目录内新建的唯一目录；拒绝清理目录链接。
            if (Directory.Exists(staging) && (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0 &&
                Path.GetFullPath(staging).StartsWith(Path.GetFullPath(catalog.RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(staging, true);
            }
            gate.Release();
        }
    }
}

namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ToolManagementService
{
    public Task<ToolArchivePreview> PreviewInstallAsync(string archive, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var path = Path.GetFullPath(archive);
            ToolchainArchiveFormat.ValidateFileName(path);
            progress?.Report("读取离线归档并记录 SHA-256…");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            using var container = ToolchainArchive.Open(file, token);
            var bytes = await container.ReadManifestAsync(token);
            var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
                ?? throw new StudioXException("TOOLS_ARCHIVE", "工具清单为空。");
            var identity = manifest.Identity;
            if (manifest.Sha256 is null || manifest.Executables is null)
            {
                throw new StudioXException("TOOLS_IDENTITY", "工具格式或宿主不支持，或清单不完整。");
            }
            container.ValidateIndex(manifest.Sha256);
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var target = PathBoundary.Resolve(catalog.RootDirectory, identity.RelativeDirectory);
            var alreadyInstalled = Directory.Exists(target) || File.Exists(target);
            if (alreadyInstalled)
            {
                var installedManifest = PathBoundary.Resolve(target, "toolset.json");
                if (!File.Exists(installedManifest) || new FileInfo(installedManifest).Length > 32 * 1024 * 1024)
                {
                    throw new StudioXException("TOOLS_REPAIR_REQUIRED", "组件版本目录已存在，但清单缺失或无效。请使用离线修复入口。");
                }
                var installedFingerprint = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installedManifest, token)));
                if (!installedFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("TOOLS_VERSION_CONFLICT", "相同开发环境组件 ID 和版本已经存在不同内容。请发布新组件版本；不会覆盖预装或已导入的组件。");
                }
            }
            var installed = new List<string>();
            foreach (var installedPath in catalog.ManifestPaths())
            {
                if (Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(installedPath))) == manifest.Id)
                {
                    installed.Add(Path.GetFileName(Path.GetDirectoryName(installedPath))!);
                }
            }
            file.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            return new ToolArchivePreview(path, hash, manifest.Id, manifest.Version, manifest.CompilerId, manifest.DisplayName ?? manifest.Id,
                container.Bytes, container.Entries.Count, Components(manifest), installed,
                fingerprint, alreadyInstalled, identity.Host);
        }, token);
    public Task<DevelopmentComponentInstallResult> InstallAsync(ToolArchivePreview preview, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                EnsureIdle();
                // 不信任调用方构造的预览；确认后重新读取归档，安装器持有同一归档的读锁。
                var fresh = await PreviewInstallAsync(preview.Archive, progress, token);
                if (fresh.ArchiveSha256 != preview.ArchiveSha256 || fresh.Fingerprint != preview.Fingerprint || fresh.Identity != preview.Identity)
                {
                    throw new StudioXException("TOOLS_CHANGED", "离线归档在预览后发生变化，请重新预览。");
                }
                if (fresh.AlreadyInstalled)
                {
                    await VerifyInstalledComponentAsync(fresh, new ThrottledProgress(progress), token);
                    progress?.Report("相同开发环境组件已安装且完整校验通过，跳过重复安装。");
                    return new DevelopmentComponentInstallResult(fresh.Identity, fresh.Fingerprint, true);
                }
                var entry = new ToolEnvironmentEntry(fresh.Id, fresh.Version, fresh.Name, fresh.CompilerId, fresh.Bytes, fresh.Files, false, "安装新版本");
                var throttled = new ThrottledProgress(progress);
                _ = await new ToolEnvironmentService(catalog).RepairAsync(entry, fresh.Archive, throttled, token, installOnly: true, expectedArchiveHash: fresh.ArchiveSha256);
                return new DevelopmentComponentInstallResult(fresh.Identity, fresh.Fingerprint, false);
            }
            finally { gate.Release(); }
        }, token);

    private async Task VerifyInstalledComponentAsync(ToolArchivePreview preview, IProgress<string> progress, CancellationToken token)
    {
        // 同身份重复导入也校验归档与已安装内容，损坏的预装组件不能被误报为就绪。
        using var lease = ToolUsageLease.Acquire(PathBoundary.Resolve(catalog.RootDirectory, preview.Identity.RelativeDirectory), ignoreActivation: true);
        await using var file = new FileStream(preview.Archive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(preview.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("TOOLS_CHANGED", "开发环境组件归档在预览后发生变化，请重新预览。");
        }
        file.Position = 0;
        using var container = ToolchainArchive.Open(file, token);
        var bytes = await container.ReadManifestAsync(token);
        var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)!;
        container.ValidateIndex(manifest.Sha256);
        await container.ReadFilesAsync(async (entry, source) =>
        {
            token.ThrowIfCancellationRequested();
            progress.Report("校验开发环境组件归档：" + entry.Name);
            using var hash = SHA256.Create();
            using var hashing = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
            await ToolchainArchive.CopyExactAsync(source, hashing, entry.Length, token);
            hashing.FlushFinalBlock();
            var actual = Convert.ToHexString(hash.Hash!);
            var expected = entry.Name == "toolset.json" ? preview.Fingerprint : manifest.Sha256[entry.Name];
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("TOOL_HASH", "开发环境组件归档文件已损坏：" + entry.Name);
            }
        }, token);
        var installed = await catalog.ResolveAsync(preview.Id, preview.Version, preview.CompilerId, token, true, progress, allowDisabled: true);
        if (!installed.Fingerprint.Equals(preview.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("TOOLS_VERSION_CONFLICT", "校验期间组件内容发生变化，未执行安装。");
        }
    }
    private sealed class ThrottledProgress(IProgress<string>? target) : IProgress<string>
    {
        private long last;
        public void Report(string value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (last == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 250)
            {
                last = now;
                target?.Report(value);
            }
        }
    }
}

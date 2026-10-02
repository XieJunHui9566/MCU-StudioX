namespace StudioX.Application.Tools;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class ToolManagementService
{
    public Task<ToolArchivePreview> PreviewInstallAsync(string archive, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var path = Path.GetFullPath(archive);
            progress?.Report("读取离线归档并记录 SHA-256…");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
            if (zip.Entries.Count is < 2 or > 200000 || zip.Entries.Sum(entry => entry.Length) > 40L * 1024 * 1024 * 1024)
                throw new StudioXException("TOOLS_ARCHIVE_SIZE", "归档文件数或展开大小超出限制。");
            var description = zip.GetEntry("toolset.json") ?? throw new StudioXException("TOOLS_ARCHIVE", "缺少 toolset.json。");
            if (description.Length > 32 * 1024 * 1024) throw new StudioXException("TOOLS_ARCHIVE_SIZE", "工具清单过大。");
            using var memory = new MemoryStream();
            await using (var source = description.Open()) { await source.CopyToAsync(memory, token); }
            var bytes = memory.ToArray();
            var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
                ?? throw new StudioXException("TOOLS_ARCHIVE", "工具清单为空。");
            PackValidator.Token(manifest.Id); PackValidator.Version(manifest.Version);
            if (manifest.FormatVersion != 1 || manifest.Host != "win-x64" || string.IsNullOrWhiteSpace(manifest.CompilerId) || manifest.Sha256 is null || manifest.Executables is null)
                throw new StudioXException("TOOLS_IDENTITY", "工具格式或宿主不支持，或清单不完整。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                _ = PathBoundary.Resolve(Path.GetTempPath(), entry.FullName);
                if (!names.Add(entry.FullName) || entry.FullName.EndsWith('/') || entry.FullName != "toolset.json" && !manifest.Sha256.ContainsKey(entry.FullName))
                    throw new StudioXException("TOOLS_ARCHIVE_ENTRY", "归档含重复、目录或未索引条目。");
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new StudioXException("TOOLS_ARCHIVE_LINK", "工具归档不接受链接。");
            }
            if (manifest.Sha256.Count != zip.Entries.Count - 1) throw new StudioXException("TOOLS_ARCHIVE_ENTRY", "索引与归档文件集合不一致。");
            var target = PathBoundary.Resolve(catalog.RootDirectory, manifest.Id + "/" + manifest.Version);
            if (Directory.Exists(target) || File.Exists(target)) throw new StudioXException("TOOLS_VERSION_EXISTS", "该版本已安装；相同版本的修复请使用工具环境的离线修复入口。");
            var installed = new List<string>();
            foreach (var installedPath in catalog.ManifestPaths())
                if (Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(installedPath))) == manifest.Id)
                    installed.Add(Path.GetFileName(Path.GetDirectoryName(installedPath))!);
            file.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            return new ToolArchivePreview(path, hash, manifest.Id, manifest.Version, manifest.CompilerId, manifest.DisplayName ?? manifest.Id,
                zip.Entries.Sum(entry => entry.Length), zip.Entries.Count, Components(manifest), installed,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }, token);
    public Task InstallAsync(ToolArchivePreview preview, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                EnsureIdle();
                // 不信任调用方构造的预览；确认后重新读取归档，安装器持有同一归档的读锁。
                var fresh = await PreviewInstallAsync(preview.Archive, progress, token);
                if (fresh.ArchiveSha256 != preview.ArchiveSha256 || fresh.Fingerprint != preview.Fingerprint || fresh.Id != preview.Id || fresh.Version != preview.Version || fresh.CompilerId != preview.CompilerId)
                    throw new StudioXException("TOOLS_CHANGED", "离线归档在预览后发生变化，请重新预览。");
                var entry = new ToolEnvironmentEntry(fresh.Id, fresh.Version, fresh.Name, fresh.CompilerId, fresh.Bytes, fresh.Files, false, "安装新版本");
                var throttled = new ThrottledProgress(progress);
                _ = await new ToolEnvironmentService(catalog).RepairAsync(entry, fresh.Archive, throttled, token, installOnly: true, expectedArchiveHash: fresh.ArchiveSha256);
            }
            finally { gate.Release(); }
        }, token);
    private sealed class ThrottledProgress(IProgress<string>? target) : IProgress<string>
    {
        private long last;
        public void Report(string value)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (last == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 250) { last = now; target?.Report(value); }
        }
    }
}

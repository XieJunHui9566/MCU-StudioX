namespace StudioX.PluginValidation;

using System.IO.Compression;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>隔离归档门禁，覆盖路径、哈希、冲突、降级与发布事务。</summary>
internal static class RepositoryChecks
{
    public static async Task RunAsync(string archive, string scratch, ValidationChecks checks)
    {
        var managed = Path.Combine(scratch, "repository");
        var repository = new PluginRepository(managed);
        var installed = await repository.ImportAsync(archive);
        checks.Check((await repository.ListAsync()).Count == 1, "archive import and full indexed catalog");
        var same = await repository.ImportAsync(archive);
        checks.Check(same.Directory == installed.Directory, "identical import is idempotent");

        var source = Path.Combine(scratch, "upgrade-source");
        CopyDirectory(installed.Directory, source);
        var manifest = installed.Manifest;
        var manifestPath = Path.Combine(source, "plugin.json");
        await JsonStore.WriteAsync(manifestPath, manifest with
        {
            Version = "1.1.0"
        });
        var upgrade = Path.Combine(scratch, "upgrade.studioxplugin");
        await PluginRepository.PackAsync(source, upgrade);
        using var cancelled = new CancellationTokenSource();
        try
        {
            _ = await repository.ImportAsync(upgrade, (_, _) =>
            {
                cancelled.Cancel();
                return Task.CompletedTask;
            }, cancelled.Token);
            throw new InvalidOperationException("取消仍然发布插件。");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
        {
            checks.Check((await repository.ListAsync()).Single().Manifest.Version == "1.0.0" &&
                !Directory.GetDirectories(managed).Any(path => Path.GetFileName(path).StartsWith('.')),
                "cancelled atomic import preserves previous version and removes staging");
        }
        _ = await repository.ImportAsync(upgrade);
        checks.Check((await repository.ListAsync()).Single().Manifest.Version == "1.1.0", "new version atomically replaces catalog");
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(archive); }, "downgrade rejected", "PLUGIN_DOWNGRADE");
        await JsonStore.WriteAsync(manifestPath, manifest with
        {
            Version = "1.1.0",
            DisplayName = "Different same version"
        });
        var conflict = Path.Combine(scratch, "conflict.studioxplugin");
        await PluginRepository.PackAsync(source, conflict);
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(conflict); }, "same version content conflict rejected", "PLUGIN_VERSION_CONFLICT");

        var traversal = await SmallArchiveAsync(scratch, "traversal", [("plugin.json", "{}"), ("../escaped.txt", "escape")]);
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(traversal); }, "ZIP traversal rejected", "PATH_UNSAFE", "PATH_ESCAPE");
        checks.Check(!File.Exists(Path.Combine(scratch, "escaped.txt")), "traversal creates no external file");
        var duplicate = await SmallArchiveAsync(scratch, "duplicate", [("plugin.json", "{}"), ("PLUGIN.JSON", "{}")]);
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(duplicate); }, "case duplicate ZIP path rejected", "PLUGIN_ARCHIVE");

        var tampered = Path.Combine(scratch, "tampered.studioxplugin");
        await using (var output = File.Create(tampered))
        {
            using (var target = new ZipArchive(output, ZipArchiveMode.Create))
            {
                await using (var input = File.OpenRead(archive))
                {
                    using (var original = new ZipArchive(input, ZipArchiveMode.Read))
                    {
                        foreach (var entry in original.Entries)
                        {
                            await using var from = entry.Open();
                            await using var to = target.CreateEntry(entry.FullName).Open();
                            if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                using var bytes = new MemoryStream();
                                await from.CopyToAsync(bytes);
                                var content = bytes.ToArray();
                                content[^1] ^= 1;
                                await to.WriteAsync(content);
                            }
                            else
                            {
                                await from.CopyToAsync(to);
                            }
                        }
                    }
                }
            }
        }
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(tampered); }, "modified archive content fails SHA-256", "PLUGIN_HASH");
        var linked = Path.Combine(scratch, "linked.studioxplugin");
        await using (var stream = File.Create(linked))
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await using (var entry = zip.CreateEntry("plugin.json").Open())
                {
                    await using (var writer = new StreamWriter(entry))
                    {
                        await writer.WriteAsync("{}");
                    }
                }
                var link = zip.CreateEntry("link.dll");
                link.ExternalAttributes = unchecked((int)(0xa000u << 16));
                await using var target = link.Open();
                await target.WriteAsync("outside.dll"u8.ToArray());
            }
        }
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(linked); }, "ZIP Unix symbolic link rejected", "PLUGIN_ARCHIVE");
        var bomb = Path.Combine(scratch, "compressed.studioxplugin");
        await using (var stream = File.Create(bomb))
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await using (var manifestEntry = zip.CreateEntry("plugin.json").Open())
                {
                    await manifestEntry.WriteAsync("{}"u8.ToArray());
                }
                await using var large = zip.CreateEntry("zeros.dll", CompressionLevel.SmallestSize).Open();
                var buffer = new byte[65536];
                for (var index = 0; index < 32; index++)
                {
                    await large.WriteAsync(buffer);
                }
            }
        }
        await checks.RejectAsync(async () => { _ = await repository.ImportAsync(bomb); }, "excessive ZIP compression ratio rejected", "PLUGIN_LIMIT");
        await repository.UninstallAsync(manifest.Id);
        checks.Check((await repository.ListAsync()).Count == 0, "uninstall removes only managed plugin directory");
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<string> SmallArchiveAsync(string scratch, string name, (string Path, string Text)[] items)
    {
        var path = Path.Combine(scratch, name + ".studioxplugin");
        await using var output = File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var item in items)
        {
            await using var stream = zip.CreateEntry(item.Path).Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(item.Text);
        }
        return path;
    }
}

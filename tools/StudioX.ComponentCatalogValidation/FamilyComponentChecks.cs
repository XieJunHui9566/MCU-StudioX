namespace StudioX.ComponentCatalogValidation;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>使用本机已有发行验证全部真实负载。1.0.1 是隔离的版本切换测试件，不能当作公开更新。</summary>
internal static class FamilyComponentChecks
{
    internal static readonly string[] Ids = ["arm.gnu", "wch.riscv", "riscv.xpack", "agm.agrv", "agm.pin-mapping", "agm.logic", "espressif.idf", "espressif.esp8266-rtos"];
    internal static async Task SdkRevisionAsync(string output, string previous, Action<bool, string> check)
    {
        var catalog = new ToolsetCatalog(Path.Combine(previous, "runtime/toolsets"));
        var management = new ToolManagementService(catalog, new(Path.Combine(output, "packs")), new(output), output);
        var rows = new List<object>();
        foreach (var (id, version, framework) in new[] { ("espressif.idf", "5.5.4", "esp-idf"), ("espressif.esp8266-rtos", "3.4.0", "esp8266-rtos-sdk") })
        {
            Console.WriteLine("SDK component revision " + id);
            var original = Path.Combine(previous, id + "-" + version + "-win-x64.mcutoolchain");
            var originalHash = await ArchiveHashAsync(original);
            var archive = Path.Combine(output, id + "-1.0.1-win-x64.mcutoolchain");
            File.Copy(original, archive, false);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("toolset.json")!;
                ToolsetManifest manifest;
                await using (var input = entry.Open()) manifest = (await JsonSerializer.DeserializeAsync<ToolsetManifest>(input, JsonStore.Options))!;
                var next = manifest with { Version = "1.0.1", DisplayName = manifest.DisplayName + " (isolated component revision)",
                    ComponentVersions = new(manifest.ComponentVersions ?? []) { [framework] = version } };
                entry.Delete();
                await using var stream = zip.CreateEntry("toolset.json", CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(stream, next, JsonStore.Options);
            }
            var preview = await management.PreviewInstallAsync(archive);
            await management.InstallAsync(preview);
            var resolved = await catalog.ResolveAsync(id, "1.0.1", preview.CompilerId, forceVerification: true);
            await EspressifSdkIdentity.ValidateAsync(resolved, new(framework, framework == "esp-idf" ? "esp32s3" : "esp8266", version));
            check(resolved.Manifest.Version == "1.0.1" && EspressifSdkIdentity.DeclaredVersion(resolved.Manifest) == version,
                id + " real archive/import independently locks component revision and unchanged SDK version");
            var afterHash = await ArchiveHashAsync(original);
            check(originalHash == afterHash, id + " original SDK archive unchanged during side-by-side revision");
            rows.Add(new { id, componentVersion = "1.0.1", sdkVersion = version, preview.ArchiveSha256, preview.Fingerprint, publicRelease = false, fixture = true });
            await JsonStore.WriteAsync(Path.Combine(output, "components.json"), rows);
        }
    }
    private static async Task<string> ArchiveHashAsync(string path)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
    internal static async Task RunAsync(string output, string originals, Action<bool, string> check)
    {
        var sourceCatalog = new ToolsetCatalog(originals);
        var installedCatalog = new ToolsetCatalog(Path.Combine(output, "runtime/toolsets"));
        var management = new ToolManagementService(installedCatalog, new(Path.Combine(output, "packs")), new(output), output);
        var rows = new List<object>();
        foreach (var id in Ids)
        {
            var sourceVersion = id == "espressif.idf" ? "5.5.4" : id == "espressif.esp8266-rtos" ? "3.4.0" : "1.0.0";
            var sourceRoot = PathBoundary.Resolve(originals, id + "/" + sourceVersion);
            var originalBytes = await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "toolset.json"));
            var offset = originalBytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var sourceManifest = JsonSerializer.Deserialize<ToolsetManifest>(originalBytes.AsSpan(offset), JsonStore.Options)!;
            Console.WriteLine("COMPONENT verify/package/import " + id);
            var source = await sourceCatalog.ResolveAsync(id, sourceVersion, sourceManifest.CompilerId);
            var isSdk = id.StartsWith("espressif.", StringComparison.Ordinal);
            var candidate = isSdk ? source.Manifest : source.Manifest with { Version = "1.0.1", DisplayName = source.Manifest.DisplayName + " (isolated migration fixture)" };
            var archive = Path.Combine(output, id + "-" + candidate.Version + "-win-x64.mcutoolchain");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("toolset.json", CompressionLevel.Fastest);
                await using (var stream = entry.Open())
                {
                    var bytes = isSdk ? originalBytes : JsonSerializer.SerializeToUtf8Bytes(candidate, JsonStore.Options);
                    await stream.WriteAsync(bytes);
                }
                foreach (var relative in candidate.Sha256.Keys)
                {
                    if (relative.StartsWith("supra/", StringComparison.Ordinal) && relative.Split('/').Any(p => p.Equals("license", StringComparison.OrdinalIgnoreCase) || p.Equals("license.txt", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Private license in component index");
                    zip.CreateEntryFromFile(PathBoundary.Resolve(source.RootDirectory, relative), relative, CompressionLevel.Fastest);
                }
            }
            var preview = await management.PreviewInstallAsync(archive);
            await management.InstallAsync(preview);
            var installed = await installedCatalog.ResolveAsync(candidate.Id, candidate.Version, candidate.CompilerId, forceVerification: true);
            check(installed.Manifest.Sha256.Count == source.Manifest.Sha256.Count && preview.Files == source.Manifest.Sha256.Count + 1,
                id + " real .mcutoolchain imports and completely validates payload and exact identity");
            check((await management.InstallAsync(preview)).AlreadyInstalled, id + " duplicate import keeps immutable installed contents");
            var afterBytes = await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "toolset.json"));
            check(originalBytes.SequenceEqual(afterBytes), id + " source manifest untouched");
            rows.Add(new { id, sourceVersion, installedVersion = candidate.Version, compiler = candidate.CompilerId, preview.Bytes, preview.Files, preview.ArchiveSha256,
                installed.Fingerprint, publicRelease = false, fixture = !isSdk });
            await JsonStore.WriteAsync(Path.Combine(output, "components.json"), rows);
        }
    }
}

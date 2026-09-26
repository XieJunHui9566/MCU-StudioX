using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

internal sealed record FixtureDevice(string Id, string[] Templates);

internal sealed record FixturePack(PackManifest Manifest, string Archive, string Sha256)
{
    public static async Task<FixturePack> CreateAsync(string output, string id, string version,
        FixtureDevice[]? deviceSpecs = null, string displayName = "Retention fixture")
    {
        deviceSpecs ??= [new("fixture-chip", ["hal"])];
        var devices = deviceSpecs.Select(spec => new DeviceDefinition(spec.Id, "Fixture MCU " + spec.Id,
            "arm", 0x08000000, 131072, 0x20000000, 20480, "fixture.toolset", "1.0.0", "fixture-gcc",
            ["-mcpu=cortex-m3", "-mthumb"], [], ["sdk/include"], ["sdk/common.c"], "link.ld", [], [],
            spec.Templates.Select(template => new ProjectTemplate(template, "Fixture " + template,
                "Offline fixture " + template, "templates/" + template + ".c")).ToArray())).ToArray();
        var manifest = new PackManifest(1, id, version, displayName, "Fixture vendor", devices);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options),
            ["sdk/common.c"] = Encoding.UTF8.GetBytes("/* SDK fixture " + version + " */\nint fixture_value;\n"),
            ["sdk/include/common.h"] = Encoding.UTF8.GetBytes("extern int fixture_value;\n"),
            ["link.ld"] = Encoding.UTF8.GetBytes("MEMORY { FLASH(rx): ORIGIN=0x08000000, LENGTH=128K }\n")
        };
        foreach (var template in deviceSpecs.SelectMany(spec => spec.Templates).Distinct())
            files.Add("templates/" + template + ".c",
                Encoding.UTF8.GetBytes("/* " + template + " */\nint main(void) { for (;;) {} }\n"));
        var hashes = files.ToDictionary(pair => pair.Key,
            pair => Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(), StringComparer.Ordinal);
        files.Add("files.sha256.json", JsonSerializer.SerializeToUtf8Bytes(hashes, JsonStore.Options));
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, id + "-" + version + ".mcupack");
        await using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes);
            }
        return new(manifest, archive, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))).ToLowerInvariant());
    }

    public static Task WriteBundleIndexAsync(string directory, params FixturePack[] packs) =>
        JsonStore.WriteAsync(Path.Combine(directory, "index.json"), packs.Select(pack => new
        {
            file = Path.GetFileName(pack.Archive), id = pack.Manifest.Id,
            version = pack.Manifest.Version, sha256 = pack.Sha256
        }).ToArray());
}

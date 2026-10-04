namespace StudioX.ProductWorkflowValidation;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application.Components;
using StudioX.Foundation;

internal static class FixtureArchives
{
    public static string Component(string root, string version, string id = "fixture.byte", bool corrupt = false, string? extra = null)
    {
        var payload = new Dictionary<string, byte[]>
        {
            ["src/component.c"] = Encoding.UTF8.GetBytes("#include \"component.h\"\nunsigned studiox_component_value(void) { volatile unsigned value = " + (version == "1.0.0" ? "42" : "43") + "; return value; }\n"),
            ["include/component.h"] = Encoding.UTF8.GetBytes("unsigned studiox_component_value(void);\n"),
            ["LICENSE.txt"] = Encoding.UTF8.GetBytes("Offline fixture; not a public library release.\n")
        };
        var manifest = new ComponentManifest(1, id, version, "Fixture byte library", "Isolated test", "NOASSERTION", "https://example.test/fixture", ["cmake", "esp-idf"], ["src/component.c"], ["include"], payload.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value))));
        if (corrupt)
        {
            payload["src/component.c"] = [0];
        }
        if (extra is not null)
        {
            payload[extra] = [1];
        }
        payload["component.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options);
        var file = Path.Combine(root, id + "-" + version + "-" + Guid.NewGuid().ToString("N") + ".studioxcomponent");
        Write(file, payload);
        return file;
    }
    public static void Write(string file, Dictionary<string, byte[]> entries)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }
}

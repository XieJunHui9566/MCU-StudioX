namespace StudioX.PluginRuntimeValidation;

using System.Security.Cryptography;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>完整读取清单和文件索引，验证各 API 版本的合法能力组合及拒绝边界。</summary>
internal static class ManifestCapabilitiesChecks
{
    public static async Task<string> RunAsync(string output, Action<bool, string> check)
    {
        var root = Path.Combine(output, "manifest-capabilities");
        Directory.CreateDirectory(root);
        const string entry = "entry.dll";
        var bytes = new byte[] { 0x4d, 0x5a, 0x01 };
        await File.WriteAllBytesAsync(Path.Combine(root, entry), bytes);
        var hashes = new Dictionary<string, string> { [entry] = Convert.ToHexString(SHA256.HashData(bytes)) };
        var manifest = new PluginManifest(1, 3, "validation.capabilities", "1.0.0", "Capabilities", entry,
            "ValidationPlugin", ["commands"], hashes);
        var path = Path.Combine(root, "plugin.json");
        string[] api2 = ["commands", "panels", "agentTools", "panelWindows", "programmerCalculator", "calculatorRealtime", "mcuConfigurationPreview"];
        string[] api3 = ["commands", "panels", "agentTools", "settings", "events", "languages", "debugAdapters", "panelWindows", "programmerCalculator", "calculatorRealtime", "mcuConfigurationPreview", "projectMatching", "projectClockV1", "mcuClockDiagramV1", "mcuCodeGenerationV1", "mcuConfigurationSaveV1", "mcuClockFieldsV1", "mcuPinActionsV1"];
        var support = new PluginProjectSupport("validation.pack", "1.0.0", "validation.device", "bare", "validation.package", "validation.profile", "1.0.0");
        await JsonStore.WriteAsync(path, manifest with
        {
            Capabilities = api3,
            SupportedProjects = [support],
            ManagesProjectClock = true
        });
        var complete = await PluginManifest.ReadAsync(path);
        check(complete.Capabilities.SequenceEqual(api3), "API 3 accepts all 18 compatible capabilities together");
        await JsonStore.WriteAsync(path, manifest with
        {
            Capabilities = api3[..11]
        });
        check((await PluginManifest.ReadAsync(path)).Capabilities.Length == 11, "API 3 accepts valid capability combination above the old ten-item limit");
        await JsonStore.WriteAsync(path, manifest with
        {
            ApiVersion = 2,
            Capabilities = api2
        });
        check((await PluginManifest.ReadAsync(path)).Capabilities.Length == api2.Length, "API 2 accepts its complete capability set");
        await JsonStore.WriteAsync(path, manifest with
        {
            ApiVersion = 1,
            Capabilities = ["decode"]
        });
        check((await PluginManifest.ReadAsync(path)).Capabilities.SequenceEqual(new[] { "decode" }), "API 1 preserves explicit decode-only compatibility");
        foreach (var (name, candidate) in new[]
        {
            ("unknown", manifest with { Capabilities = ["commands", "unknown"] }),
            ("duplicate", manifest with { Capabilities = ["commands", "commands"] }),
            ("cross-version", manifest with { ApiVersion = 2, Capabilities = ["settings"] }),
            ("legacy-version", manifest with { ApiVersion = 1, Capabilities = ["commands"] }),
            ("empty", manifest with { Capabilities = [] }),
            ("too-many", manifest with { Capabilities = api3.Append("unknown").ToArray() })
        })
        {
            await JsonStore.WriteAsync(path, candidate);
            try
            {
                _ = await PluginManifest.ReadAsync(path);
            }
            catch (StudioXException exception) when (exception.Code == "PLUGIN_MANIFEST")
            {
                check(true, "manifest rejects " + name + " capabilities");
                continue;
            }
            throw new InvalidOperationException("Manifest accepted invalid capability set: " + name);
        }
        const string summary = "Manifest capabilities: full API 1/2/3 sets, 11-capability combination and unknown/duplicate/cross-version/empty/overflow rejection passed";
        await JsonStore.WriteAsync(Path.Combine(output, "manifest-capabilities.json"), new
        {
            success = true,
            summary
        });
        return summary;
    }
}

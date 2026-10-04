namespace StudioX.ProductWorkflowValidation;

using System.IO.Compression;
using System.Text.Json.Nodes;
using StudioX.Application.Plugins;
using StudioX.Foundation;

internal static class PluginChecks
{
    public static async Task RunAsync(string root, string pluginArchive, Action<bool, string> check)
    {
        var runtime = Path.Combine(root, "plugin-runtime");
        await using var manager = new PluginManagerService(runtime, Path.Combine(root, "plugin-data"));
        var one = await manager.ImportAsync(pluginArchive);
        await manager.SetEnabledAsync(one.Id, true);
        var payload = new Dictionary<string, byte[]>();
        using (var zip = ZipFile.OpenRead(pluginArchive))
        {
            foreach (var entry in zip.Entries)
            {
                using var memory = new MemoryStream();
                using var stream = entry.Open();
                stream.CopyTo(memory);
                payload[entry.FullName] = memory.ToArray();
            }
        }
        var node = JsonNode.Parse(payload["plugin.json"])!;
        node["version"] = "1.0.1";
        payload["plugin.json"] = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        var second = Path.Combine(root, "plugin-1.0.1.studioxplugin");
        FixtureArchives.Write(second, payload);
        var updated = await manager.ImportAsync(second);
        check(updated.Version == "1.0.1" && !updated.Enabled, "plugin update revokes existing trust instead of running replacement code");
        var versions = await manager.ListRollbackAsync(one.Id);
        check(versions.Any(v => v.Version == one.Version), "plugin update creates a validated rollback archive");
        var previous = versions.Single(v => v.Version == one.Version);
        var restored = await manager.RollbackAsync(previous);
        check(restored.Version == one.Version && !restored.Enabled, "explicit plugin rollback restores bytes but requires trust again");
        await File.AppendAllTextAsync(previous.Archive, "changed");
        try
        {
            await manager.RollbackAsync(previous);
            check(false, "rollback archive changed");
        }
        catch (StudioXException error) { check(error.Code == "PLUGIN_ROLLBACK_CHANGED", "changed rollback archive rejected before replacing current plugin"); }
    }
}

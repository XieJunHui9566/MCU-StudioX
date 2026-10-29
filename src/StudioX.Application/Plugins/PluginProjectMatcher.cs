namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>在启动宿主之前核对工程与配置身份；不执行插件，不按厂商前缀猜测适用性。</summary>
public static class PluginProjectMatcher
{
    public static async Task<bool> MatchesAsync(PluginManifest manifest, string? root, CancellationToken token = default)
    {
        if (manifest.SupportedProjects is null && !manifest.Capabilities.Contains("mcuConfigurationPreview", StringComparer.Ordinal))
        {
            return manifest.Scope == (root is null ? "application" : "project");
        }
        if (root is null)
        {
            return false;
        }
        try
        {
            var project = await ProjectService.ReadAsync(root, token);
            if (project.Kind != ProjectKind.Pack || project.GraphicalConfiguration?.Path != "device.mcuconfig")
            {
                return false;
            }
            var path = PathBoundary.Resolve(root, project.GraphicalConfiguration.Path);
            if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024)
            {
                return false;
            }
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token), new JsonDocumentOptions { MaxDepth = 32 });
            _ = CanonicalJson.Encode(json.RootElement);
            var doc = json.RootElement;
            var target = doc.GetProperty("target");
            var profile = doc.GetProperty("profile");
            var origin = doc.GetProperty("origin");
            return doc.GetProperty("format").GetString() == "studiox.mcuconfig" && doc.GetProperty("formatVersion").GetInt32() == 1 &&
                doc.GetProperty("documentKind").GetString() == "project" && profile.GetProperty("pluginId").GetString() == manifest.Id &&
                target.GetProperty("deviceId").GetString() == project.DeviceId && origin.GetProperty("deviceId").GetString() == project.DeviceId &&
                origin.GetProperty("packId").GetString() == project.PackId && origin.GetProperty("packVersion").GetString() == project.PackVersion &&
                origin.GetProperty("packContentHash").GetString() == project.PackContentHash && origin.GetProperty("templateId").GetString() == project.TemplateId &&
                (manifest.SupportedProjects is null || manifest.SupportedProjects.Any(item => item.PackId == project.PackId && item.PackVersion == project.PackVersion &&
                    item.DeviceId == project.DeviceId && item.TemplateId == project.TemplateId && item.PackageId == target.GetProperty("packageId").GetString() &&
                    item.ProfileId == profile.GetProperty("id").GetString() && item.ProfileVersion == profile.GetProperty("version").GetString()));
        }
        catch (Exception error) when (error is IOException or JsonException or StudioXException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }
}

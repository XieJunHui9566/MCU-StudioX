namespace StudioX.Packages;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Foundation;

/// <summary>校验配置模板身份并建立工程来源；器件和 SDK 规则由配置插件解释。</summary>
public static class GraphicalConfigurationFile
{
    public static JsonObject ReadTemplate(string root, string file, string deviceId)
    {
        var path = PathBoundary.Resolve(root, file);
        if (!file.EndsWith(".mcuconfig", StringComparison.Ordinal) || !System.IO.File.Exists(path) ||
            new FileInfo(path).Length > 512 * 1024)
        {
            throw new StudioXException("MCUCONFIG_FORMAT", "配置模板路径、扩展名或容量无效。");
        }
        var bytes = System.IO.File.ReadAllBytes(path);
        if (bytes is [0xef, 0xbb, 0xbf, ..])
        {
            throw new StudioXException("MCUCONFIG_FORMAT", "配置文件不能带 BOM。");
        }
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateNumbers(parsed.RootElement);
        var canonical = CanonicalJson.Encode(parsed.RootElement);
        if (canonical.Length > 256 * 1024)
        {
            throw new StudioXException("MCUCONFIG_LIMIT", "配置规范化内容超过 256 KiB。");
        }
        var value = JsonNode.Parse(bytes) as JsonObject ?? throw new StudioXException("MCUCONFIG_FORMAT", "配置根必须为对象。");
        var names = new[] { "format", "formatVersion", "documentKind", "target", "profile", "origin", "definitions", "configuration", "generation" };
        if (value.Count != names.Length || names.Any(name => !value.ContainsKey(name)) ||
            value["format"]?.GetValue<string>() != "studiox.mcuconfig" || value["formatVersion"]?.GetValue<int>() != 1 ||
            value["documentKind"]?.GetValue<string>() != "template" || value["origin"] is not null || value["generation"] is not null ||
            value["target"]?["deviceId"]?.GetValue<string>() != deviceId ||
            value["target"]?["packageId"]?.GetValue<string>() != value["definitions"]?["package"]?["id"]?.GetValue<string>() ||
            value["profile"] is not JsonObject || value["configuration"] is not JsonObject ||
            value["definitions"]?["pins"] is not JsonArray pins || pins.Count != value["definitions"]?["package"]?["pinCount"]?.GetValue<int>())
        {
            throw new StudioXException("MCUCONFIG_TARGET", "配置模板结构、来源状态或准确器件/封装身份不符。");
        }
        return value;
    }

    private static void ValidateNumbers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Abs(number) > 9007199254740991d ||
             value.TryGetDecimal(out var exact) && Math.Abs(exact) > 9007199254740991m))
        {
            throw new StudioXException("MCUCONFIG_NUMBER", "配置数值超过公共契约的精确范围。");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                ValidateNumbers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateNumbers(item);
            }
        }
    }

    public static async Task CreateProjectCopyAsync(InstalledPack pack, string deviceId, ProjectTemplate template,
        string destination, CancellationToken token)
    {
        if (template.GraphicalConfiguration is not { } graphical)
        {
            return;
        }
        var value = ReadTemplate(pack.RootDirectory, graphical.File, deviceId);
        var templateBytes = await System.IO.File.ReadAllBytesAsync(PathBoundary.Resolve(pack.RootDirectory, graphical.File), token);
        value["documentKind"] = "project";
        value["origin"] = JsonSerializer.SerializeToNode(new
        {
            packId = pack.Manifest.Id,
            packVersion = pack.Manifest.Version,
            packContentHash = pack.ContentHash,
            deviceId,
            templateId = template.Id,
            templateFile = graphical.File,
            templateSha256 = Convert.ToHexStringLower(SHA256.HashData(templateBytes)),
            definitionsSha256 = CanonicalJson.Hash(JsonSerializer.SerializeToElement(value["definitions"]))
        });
        var path = PathBoundary.Resolve(destination, "device.mcuconfig");
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, value, JsonStore.Options, token);
    }
}

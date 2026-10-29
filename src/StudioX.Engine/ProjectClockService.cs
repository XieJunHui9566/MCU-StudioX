namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>读取版本化时钟绑定和已保存的时钟树；只计算声明的单级分频，不求解芯片规则。</summary>
public sealed class ProjectClockService
{
    public IProjectClockOwnership? Ownership
    {
        get; set;
    }
    public Func<string, CancellationToken, Task<bool>>? HasPendingChangesAsync
    {
        get; set;
    }

    public Task<ProjectClockState> ReadAsync(string root, CancellationToken token = default) => ReadCoreAsync(root, null, token);

    /// <summary>生成事务校验尚未写入的候选配置；仍核对器件包来源和时钟凭据。</summary>
    public Task<ProjectClockState> ValidateCandidateAsync(string root, byte[] candidate, CancellationToken token = default)
        => ReadCoreAsync(root, candidate, token);

    private async Task<ProjectClockState> ReadCoreAsync(string root, byte[]? candidate, CancellationToken token)
    {
        var required = Ownership is not null && await Ownership.RequiresManagedClockAsync(root, token);
        var project = await ProjectService.ReadAsync(root, token);
        if (project.GraphicalConfiguration is not { } association)
        {
            return new(false, true, "时钟由下载设置管理。");
        }
        var managed = required;
        string? plugin = null;
        try
        {
            var deviceRoot = PathBoundary.Resolve(root, "device");
            var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(deviceRoot, "manifest.json"), token);
            var templateFile = pack.Devices.Single(device => device.Id == project.DeviceId).Templates.Single(template => template.Id == project.TemplateId).GraphicalConfiguration?.File;
            if (pack.Id != project.PackId || pack.Version != project.PackVersion || templateFile is null)
            {
                throw Invalid("工程与时钟模板绑定不一致。");
            }
            var templatePath = PathBoundary.Resolve(deviceRoot, templateFile);
            if (new FileInfo(templatePath).Length > 512 * 1024)
            {
                throw Invalid("时钟模板容量异常。");
            }
            var templateBytes = await File.ReadAllBytesAsync(templatePath, token);
            var template = JsonNode.Parse(templateBytes)!;
            if (association.Path != "device.mcuconfig")
            {
                throw Invalid("图形配置关联路径无效。");
            }
            var path = PathBoundary.Resolve(root, association.Path);
            if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024)
            {
                throw Invalid("请先保存工程图形配置。");
            }
            if (candidate is null && File.Exists(PathBoundary.Resolve(root, ".studiox/mcu-generation.pending.json")))
            {
                throw Invalid("代码生成事务尚未完成；请在原用户目录重新打开工程以恢复。");
            }
            var bytes = candidate ?? await File.ReadAllBytesAsync(path, token);
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            _ = CanonicalJson.Encode(parsed.RootElement);
            var doc = JsonNode.Parse(bytes)!.AsObject();
            plugin = doc["profile"]?["pluginId"]?.GetValue<string>();
            if (doc["configuration"]?["profileData"]?["projectClock"] is not JsonObject policy)
            {
                if (required)
                {
                    throw Invalid("此配置尚未提供时钟托管契约，请使用匹配的时钟托管模板。");
                }
                return new(false, true, "时钟由下载设置管理。");
            }
            managed |= policy["managed"]!.GetValue<bool>();
            if (!managed)
            {
                return new(false, true, "时钟由下载设置管理。", plugin);
            }
            if (candidate is null && HasPendingChangesAsync is not null && await HasPendingChangesAsync(root, token))
            {
                return new(true, false, "图形时钟配置有尚未应用或保存的修改，请先检查并保存。", plugin);
            }
            var origin = doc["origin"]!;
            if (doc["format"]?.GetValue<string>() != "studiox.mcuconfig" || doc["formatVersion"]?.GetValue<int>() != 1 ||
                doc["documentKind"]?.GetValue<string>() != "project" || doc["target"]?["deviceId"]?.GetValue<string>() != project.DeviceId ||
                origin["packId"]?.GetValue<string>() != project.PackId || origin["packVersion"]?.GetValue<string>() != project.PackVersion ||
                origin["packContentHash"]?.GetValue<string>() != project.PackContentHash || origin["deviceId"]?.GetValue<string>() != project.DeviceId ||
                origin["templateId"]?.GetValue<string>() != project.TemplateId)
            {
                throw Invalid("时钟配置与当前工程来源不一致。");
            }
            var indexPath = PathBoundary.Resolve(root, ".studiox/pack-files.sha256.json");
            if (new FileInfo(indexPath).Length > 8 * 1024 * 1024)
            {
                throw Invalid("器件包索引容量异常。");
            }
            var index = await JsonStore.ReadAsync<Dictionary<string, string>>(indexPath, token);
            var indexIdentity = string.Join('\n', index.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}\t{item.Value.ToLowerInvariant()}"));
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(indexIdentity))) != project.PackContentHash ||
                origin["templateFile"]!.GetValue<string>() != templateFile || !index.TryGetValue(templateFile, out var indexedTemplate) ||
                indexedTemplate != origin["templateSha256"]!.GetValue<string>() ||
                Convert.ToHexStringLower(SHA256.HashData(templateBytes)) != origin["templateSha256"]!.GetValue<string>() ||
                !JsonNode.DeepEquals(template["definitions"], doc["definitions"]) || !JsonNode.DeepEquals(template["profile"], doc["profile"]) ||
                !JsonNode.DeepEquals(template["target"], doc["target"]) ||
                CanonicalJson.Hash(JsonSerializer.SerializeToElement(doc["definitions"])) != origin["definitionsSha256"]!.GetValue<string>())
            {
                throw Invalid("时钟基础资料或模板来源已变化。");
            }
            var binding = doc["definitions"]!["profileData"]!["projectClock"]!;
            if (binding["contractVersion"]?.GetValue<int>() != 1 || policy["valid"]?.GetValue<bool>() != true ||
                policy["inputSha256"]?.GetValue<string>() != InputHash(doc))
            {
                throw Invalid("图形配置尚未通过保存校验，或文件已被外部修改；请返回配置器检查并保存。");
            }
            if (candidate is null && doc["generation"] is JsonObject generation)
            {
                if (generation["inputSha256"]?.GetValue<string>() != InputHash(doc))
                {
                    throw Invalid("图形配置已变化，请重新生成代码。");
                }
                foreach (var output in generation["outputs"]!.AsArray())
                {
                    var outputPath = output!["path"]!.GetValue<string>();
                    if (!(outputPath.StartsWith("src/generated/mcuconfig/", StringComparison.Ordinal) || outputPath.StartsWith("include/generated/mcuconfig/", StringComparison.Ordinal)) ||
                        !outputPath.EndsWith(".c", StringComparison.Ordinal) && !outputPath.EndsWith(".h", StringComparison.Ordinal))
                    {
                        throw Invalid("生成文件凭据路径无效。");
                    }
                    var outputBytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, outputPath), token);
                    if (outputBytes.Length > 128 * 1024 || Convert.ToHexStringLower(SHA256.HashData(outputBytes)) != output["sha256"]!.GetValue<string>())
                    {
                        throw Invalid("生成文件已变化：" + outputPath);
                    }
                }
            }
            var clocks = doc["configuration"]!["clocks"]!.AsArray();
            if (clocks.Select(node => node!["nodeId"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != clocks.Count)
            {
                throw Invalid("时钟节点重复。");
            }
            var system = clocks.Single(node => node!["nodeId"]!.GetValue<string>() == binding["systemNodeId"]!.GetValue<string>())!;
            var sourceId = system["inputId"]!.GetValue<string>();
            var source = clocks.Single(node => node!["nodeId"]!.GetValue<string>() == sourceId)!;
            var kind = binding["sourceKinds"]![sourceId]!.GetValue<string>();
            var frequency = source["parameters"]!["frequencyHz"]!.GetValue<int>();
            var divider = system["parameters"]!["divider"]!.GetValue<int>();
            if (system["enabled"]!.GetValue<bool>() != true || source["enabled"]!.GetValue<bool>() != true ||
                frequency is < 1 or > 40000000 || divider is < 1 or > 1024 || frequency % divider != 0 || frequency / divider == 0 ||
                kind is not ("internalRc" or "externalCrystal"))
            {
                throw Invalid("时钟源、整数频率或系统分频无效。");
            }
            return new(true, true, $"时钟由图形配置托管：源 {frequency} Hz / {divider} = 系统 {frequency / divider} Hz。", plugin,
                kind, frequency, frequency / divider, divider, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException or FormatException or IOException or StudioXException)
        {
            return new(true, false, "图形时钟配置不可用：" + error.Message, plugin);
        }
    }

    /// <summary>校验投影不包含自身凭据；文本外部编辑后不能继续使用旧的校验结果。</summary>
    public static string InputHash(JsonObject document)
    {
        var projection = (JsonObject)document.DeepClone();
        var policy = projection["configuration"]!["profileData"]!["projectClock"]!.AsObject();
        policy.Remove("inputSha256");
        policy.Remove("valid");
        projection["generation"] = null;
        return CanonicalJson.Hash(JsonSerializer.SerializeToElement(projection));
    }

    private static StudioXException Invalid(string message) => new("PROJECT_CLOCK", message);
}

namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public sealed partial class McuCodeGenerationService
{
    /// <summary>显式保存按钮的应用入口。候选计划本身不授权写入，也不开放任意工程文件。</summary>
    public async Task SaveConfigurationAsync(PluginWorkspaceSession workspace, string pluginId, string commandId,
        Func<Task<bool>> hasDirtyBuffers, CancellationToken token = default)
    {
        var root = workspace.Project ?? throw Invalid("保存配置需要工程。");
        var gate = Gates.GetOrAdd(Path.GetFullPath(root), _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(root, token).ConfigureAwait(false);
            var entry = await ProviderAsync();
            var result = await workspace.InvokeAsync(pluginId, "command", commandId,
                JsonSerializer.SerializeToElement(new
                {
                    action = "save"
                }), token).ConfigureAwait(false);
            PluginContributionValidator.ValidateJson(result, 256 * 1024);
            var plan = result.Deserialize<PluginMcuSavePlan>(JsonStore.Options) ?? throw Invalid("保存计划为空。");
            if (plan.FormatVersion != 1)
            {
                throw Invalid("保存计划版本不受支持。");
            }
            var current = await files.ReadAsync(root, "device.mcuconfig", token).ConfigureAwait(false);
            if (current.IsReadOnly || !current.DiskHash.Equals(plan.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("配置文件已变化或为只读；请保留草稿并重新读取。");
            }
            var old = JsonNode.Parse(current.Text)!.AsObject();
            var candidate = JsonNode.Parse(plan.Configuration.GetRawText())!.AsObject();
            foreach (var key in new[] { "format", "formatVersion", "documentKind", "origin", "target", "profile", "definitions", "generation" })
            {
                if (!JsonNode.DeepEquals(old[key], candidate[key]))
                {
                    throw Invalid("保存计划改变了受保护字段：" + key);
                }
            }
            var policy = candidate["configuration"]?["profileData"]?["projectClock"];
            if (candidate["profile"]?["pluginId"]?.GetValue<string>() != pluginId ||
                policy?["managed"]?.GetValue<bool>() != true || policy["inputSha256"]?.GetValue<string>() != ProjectClockService.InputHash(candidate))
            {
                throw Invalid("配置提供者或时钟校验凭据无效。");
            }
            var bytes = Utf8.GetBytes(candidate.ToJsonString(JsonStore.Options) + "\n");
            if (bytes.Length > 256 * 1024)
            {
                throw Invalid("配置文件超过保存容量。");
            }
            // 尚未完成的配置允许保存，但不能被当成可构建的有效时钟。
            if (policy["valid"]?.GetValue<bool>() == true)
            {
                var validation = await clocks.ValidateCandidateAsync(root, bytes, token).ConfigureAwait(false);
                if (!validation.Managed || !validation.Ready)
                {
                    throw Invalid(validation.Message);
                }
            }
            var before = await BytesAsync(root, "device.mcuconfig", token).ConfigureAwait(false);
            if (!string.Equals(Hash(before), current.DiskHash, StringComparison.OrdinalIgnoreCase) ||
                (await ProviderAsync()).ContentFingerprint != entry.ContentFingerprint)
            {
                throw Invalid("保存期间配置或插件发生变化。");
            }
            await CommitAsync(root, [new("device.mcuconfig", before, bytes)], hasDirtyBuffers, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        async Task<PluginCatalogEntry> ProviderAsync()
        {
            token.ThrowIfCancellationRequested();
            var provider = (await plugins.ListAsync(token).ConfigureAwait(false)).Single(item => item.Id == pluginId);
            if (await hasDirtyBuffers().ConfigureAwait(false) || !workspace.IsPluginRunning(pluginId) || !provider.Enabled ||
                !provider.Capabilities.Contains("mcuConfigurationSaveV1", StringComparer.Ordinal) || !workspace.MatchesCatalogEntry(provider) ||
                !await PluginProjectMatcher.MatchesAsync(provider.Manifest!, root, token).ConfigureAwait(false))
            {
                throw Invalid("请先保存文本编辑器修改，并确认配置插件仍属于当前工程。");
            }
            return provider;
        }
    }

    public async Task RefreshSavedConfigurationAsync(PluginWorkspaceSession workspace, string pluginId, string commandId, CancellationToken token = default)
    {
        var source = await files.ReadAsync(workspace.Project!, "device.mcuconfig", token).ConfigureAwait(false);
        var saved = JsonNode.Parse(source.Text)!.AsObject();
        await workspace.InvokeAsync(pluginId, "command", commandId, JsonSerializer.SerializeToElement(new
        {
            action = "saveApplied",
            inputSha256 = ProjectClockService.InputHash(saved)
        }), token).ConfigureAwait(false);
    }
}

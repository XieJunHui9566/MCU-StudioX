namespace StudioX.Application.Tools;

using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>用器件包中的显式目标、模板及精确 SDK 建立选择；打开页面只读元数据，不扫描 SDK 或启动工具。</summary>
public sealed class EspressifProjectVersionService(PackRepository packs, ToolsetCatalog catalog)
{
    public Task<IReadOnlyList<EspressifProjectVersionChoice>> ListAsync(InstalledPack selected, string deviceId, string templateId,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var device = selected.Manifest.Devices.Single(item => item.Id == deviceId);
        if (device.Espressif is not { Framework: "esp-idf" } profile)
        {
            return (IReadOnlyList<EspressifProjectVersionChoice>)[];
        }
        var choices = new List<EspressifProjectVersionChoice>();
        var catalogPacks = await packs.ListCatalogAsync(token);
        foreach (var pack in catalogPacks.Where(item => item.Manifest.Id == selected.Manifest.Id)
            .OrderByDescending(item => item.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)))
        {
            var candidate = pack.Manifest.Devices.SingleOrDefault(item => item.Id == deviceId && item.Espressif?.Framework == "esp-idf"
                && item.Espressif.Target == profile.Target && item.Templates.Any(template => template.Id == templateId));
            if (candidate?.Espressif is not { } sdk)
            {
                continue;
            }
            if (choices.Any(item => item.SdkVersion == sdk.SdkVersion && item.ComponentVersion == candidate.ToolsetVersion))
            {
                continue;
            }
            var manifestPath = PathBoundary.Resolve(catalog.RootDirectory, candidate.ToolsetId + "/" + candidate.ToolsetVersion + "/toolset.json");
            var state = EspressifVersionState.Missing;
            var message = "可创建并锁定此版本；编译前请导入对应 .mcutoolchain，或通过“准备工程开发环境组件”安装此精确版本。";
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifest = await ReadManifestAsync(manifestPath, token);
                    if (manifest.Id != candidate.ToolsetId || manifest.Version != candidate.ToolsetVersion || manifest.CompilerId != candidate.CompilerId
                        || manifest.Host != "win-x64" || manifest.FormatVersion != 1 || manifest.Purpose != "esp-idf"
                        || EspressifSdkIdentity.DeclaredVersion(manifest) != sdk.SdkVersion)
                    {
                        throw new StudioXException("TOOLSET_INCOMPATIBLE", "组件身份或内部 SDK 版本与器件包不一致。");
                    }
                    var resolved = new ResolvedToolset(manifest, Path.GetDirectoryName(manifestPath)!, "");
                    await EspressifSdkIdentity.ValidateAsync(resolved, new(sdk.Framework, sdk.Target, sdk.SdkVersion), token);
                    resolved = resolved.ForEspressifTarget(sdk.Target);
                    foreach (var role in new[] { "python", "cmake", "ninja", "gcc", "gxx", "objcopy", "size" })
                    {
                        var entry = resolved.Tool(role);
                        if (!File.Exists(entry) || !manifest.Sha256.ContainsKey(Path.GetRelativePath(resolved.RootDirectory, entry).Replace('\\', '/')))
                        {
                            throw new StudioXException("TOOL_MISSING", "工具入口缺失或未索引：" + role);
                        }
                    }
                    state = catalog.IsEnabled(manifest.Id, manifest.Version) ? EspressifVersionState.Ready : EspressifVersionState.Disabled;
                    message = state == EspressifVersionState.Ready
                        ? "新工程锁定此 SDK 和组件版本。安装其他版本不会改变本工程；构建前完整校验组件。"
                        : "此精确版本已禁用，请先在“开发环境组件管理”中启用。";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { state = EspressifVersionState.Invalid; message = error.ToString(); }
            }
            choices.Add(new(sdk.SdkVersion, candidate.ToolsetVersion, pack, deviceId, templateId, state, message));
        }
        foreach (var path in catalog.ManifestPaths().Where(path => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) == "espressif.idf"))
        {
            token.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, token);
            if (manifest.Purpose != "esp-idf" || manifest.Host != "win-x64")
            {
                continue;
            }
            var sdk = EspressifSdkIdentity.DeclaredVersion(manifest);
            if (choices.Any(item => item.SdkVersion == sdk && item.ComponentVersion == manifest.Version))
            {
                continue;
            }
            choices.Add(new(sdk, manifest.Version, null, deviceId, templateId, EspressifVersionState.PackMissing,
                $"组件已安装，但缺少 {deviceId} / {templateId} 对应 IDF {sdk} 的器件包。请导入匹配 .mcupack 后再选择；不能套用其他 SDK 的模板。"));
        }
        return (IReadOnlyList<EspressifProjectVersionChoice>)choices.OrderByDescending(item => item.SdkVersion,
            Comparer<string>.Create(PackVersion.Compare)).ThenByDescending(item => item.ComponentVersion, Comparer<string>.Create(PackVersion.Compare)).ToArray();
    }, token);

    private static async Task<ToolsetManifest> ReadManifestAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
        {
            throw new StudioXException("TOOLSET_MANIFEST", "工具清单过大。");
        }
        return await JsonStore.ReadAsync<ToolsetManifest>(path, token);
    }
    public async Task EnsureSelectionAsync(EspressifProjectVersionChoice selected, CancellationToken token = default)
    {
        if (selected.Pack is null)
        {
            throw new StudioXException("ESPRESSIF_VERSION_SELECTION", selected.Message);
        }
        var current = (await ListAsync(selected.Pack, selected.DeviceId, selected.TemplateId, token))
            .SingleOrDefault(item => item.Pack?.Manifest.Version == selected.Pack.Manifest.Version && item.SdkVersion == selected.SdkVersion
                && item.ComponentVersion == selected.ComponentVersion);
        if (current?.CanCreate != true)
        {
            throw new StudioXException("ESPRESSIF_VERSION_SELECTION", current?.Message ?? "版本选择发生变化，请重新选择。");
        }
    }
}

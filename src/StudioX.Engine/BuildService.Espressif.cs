namespace StudioX.Engine;

using StudioX.Foundation;

public sealed partial class BuildService
{
    public async Task<EspressifModuleSettings> LoadEspressifModuleSettingsAsync(string root, CancellationToken token = default) =>
        (await EspressifModuleConfiguration.ReadAsync(Path.GetFullPath(root), token)).Settings;

    public async Task<IReadOnlyList<EspressifModuleProfile>> ListEspressifModuleProfilesAsync(string root, CancellationToken token = default)
    {
        var configuration = await EspressifModuleConfiguration.ReadAsync(Path.GetFullPath(root), token, loadSettings: false);
        return EspressifModuleCatalog.ForTarget(configuration.Project.Espressif!.Target);
    }

    public async Task<EspressifModuleCapabilities> ReadEspressifModuleCapabilitiesAsync(string root, CancellationToken token = default)
    {
        var configuration = await EspressifModuleConfiguration.ReadAsync(Path.GetFullPath(root), token, loadSettings: false);
        return EspressifModuleCapabilities.ForTarget(configuration.Project.Espressif!.Target);
    }

    public async Task SaveEspressifModuleSettingsAsync(string root, EspressifModuleSettings settings, CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) { throw new StudioXException("BUILD_BUSY", "构建期间不能修改模块配置。"); }
        try
        {
            root = Path.GetFullPath(root);
            var configuration = await EspressifModuleConfiguration.ReadAsync(root, token, loadSettings: false);
            EspressifModuleConfiguration.Validate(settings, configuration.Project.Espressif!.Target, configuration.Device.FlashBytes);
            await JsonStore.WriteAsync(PathBoundary.Resolve(root, EspressifModuleSettings.RelativePath), settings, token);
            foreach (var relative in new[] { BuildReceipt.RelativePath, BuildMemoryService.SnapshotPath })
            {
                var path = PathBoundary.Resolve(root, relative);
                if (File.Exists(path)) { File.Delete(path); }
            }
        }
        finally { gate.Release(); }
    }
}

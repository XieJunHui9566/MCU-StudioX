namespace StudioX.Application.Lvgl;

using StudioX.Engine.Lvgl;
using StudioX.Foundation;

public sealed partial class LvglPreviewService
{
    private readonly LvglProjectInspector inspector = new();

    public Task<LvglDiscoveryReport> DiscoverAsync(string project, IReadOnlyList<string>? searchDirectories = null,
        CancellationToken token = default) => inspector.DiscoverAsync(project, searchDirectories, token);

    public Task<LvglUiInspection> InspectUiAsync(string project, string libraryDirectory, string uiDirectory,
        CancellationToken token = default) => inspector.InspectUiAsync(project, libraryDirectory, uiDirectory, token);

    public Task<LvglSetupValidation> ValidateSetupAsync(string project, LvglPreviewConfiguration configuration,
        CancellationToken token = default) => inspector.ValidateSetupAsync(project, configuration, token);

    public Task<LvglPreviewConfiguration?> ReadConfigurationIfPresentAsync(string project, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(Path.GetFullPath(project), LvglPreviewBuilder.ConfigurationPath);
        if (!File.Exists(path))
        {
            return Task.FromResult<LvglPreviewConfiguration?>(null);
        }
        if (new FileInfo(path).Length > 128 * 1024)
        {
            throw new StudioXException("LVGL_CONFIGURATION_SIZE", "LVGL 预览配置过大。");
        }
        return ReadPresentAsync();

        // 编辑向导必须能载入尚未修好的配置，完整校验在保存和启动时执行。
        async Task<LvglPreviewConfiguration?> ReadPresentAsync()
        {
            var value = await JsonStore.ReadAsync<LvglPreviewConfiguration>(path, token).ConfigureAwait(false);
            return value with
            {
                LvglDirectory = value.LvglDirectory ?? "",
                ConfigurationHeader = value.ConfigurationHeader ?? "",
                EntryPoint = value.EntryPoint ?? "",
                SourceFiles = value.SourceFiles ?? [],
                IncludeDirectories = value.IncludeDirectories ?? []
            };
        }
    }
}

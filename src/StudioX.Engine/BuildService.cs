namespace StudioX.Engine;

using StudioX.Foundation;

public sealed partial class BuildService(ToolsetCatalog catalog, string? ag32MappingLicenseDirectory = null, string? userDataDirectory = null)
{
    public ProjectClockService ProjectClocks { get; set; } = new();
    public Pic.PicBuildService Pic
    {
        get;
    } = new(catalog,
        userDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX"));
    public Arduino.ArduinoBuildService Arduino
    {
        get;
    } = new(catalog,
        userDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX"));
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool IsBusy => gate.CurrentCount == 0;
    public IDisposable AcquireMaintenance(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!gate.Wait(0))
        {
            throw new StudioXException("BUILD_BUSY", "构建或工程维护正在进行。");
        }
        return new MaintenanceLease(gate);
    }
    private sealed class MaintenanceLease(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
    public Task<ProjectBuildSettings> LoadSettingsAsync(string directory, CancellationToken token = default) => ProjectBuildSettings.ReadAsync(directory, token);
    public async Task<StcCodeRomLimit?> ReadStcCodeRomLimitAsync(string directory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        return await StcCodeRomLimit.ReadAsync(root, await ProjectService.ReadAsync(root, token), token);
    }
    public async Task SaveSettingsAsync(string directory, ProjectBuildSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("BUILD_BUSY", "构建期间不能修改编译参数。");
        }
        try
        {
            var root = Path.GetFullPath(directory);
            var project = await ProjectService.ReadAsync(root, token);
            var capabilities = ProjectCapabilities.For(project);
            if (!capabilities.BuildSettings || project.ToolsetId == Mx251Profile.ToolsetId)
            {
                throw new StudioXException(capabilities.SettingsError ?? "BUILD_SETTINGS_UNSUPPORTED", capabilities.SourceWorkflow);
            }
            settings.ValidateFor(project);
            (await StcCodeRomLimit.ReadAsync(root, project, token))?.Validate(settings.CodeRomSizeBytes);
            if (await ProjectBuildSettings.ReadAsync(root, token) == settings)
            {
                return;
            }
            await JsonStore.WriteAsync(PathBoundary.Resolve(root, ProjectBuildSettings.RelativePath), settings, token);
            foreach (var relative in new[] { BuildReceipt.RelativePath, BuildMemoryService.SnapshotPath })
            {
                var path = PathBoundary.Resolve(root, relative);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        finally { gate.Release(); }
    }
    public Task<BuildReport> BuildAsync(string projectDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default, IProgress<string>? output = null)
        => Task.Run(() => ExecuteAsync(projectDirectory, false, progress, cancellationToken, output), cancellationToken);
    public Task<BuildReport> ConfigureAsync(string projectDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default, IProgress<string>? output = null)
        => Task.Run(() => ExecuteAsync(projectDirectory, true, progress, cancellationToken, output), cancellationToken);
    private async Task<BuildReport> ExecuteAsync(string projectDirectory, bool configureOnly, IProgress<string>? progress, CancellationToken cancellationToken, IProgress<string>? output)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            throw new StudioXException("BUILD_BUSY", "构建正在进行。");
        }
        try
        {
            var root = Path.GetFullPath(projectDirectory);
            var project = await ProjectService.ReadAsync(root, cancellationToken);
            var capabilities = ProjectCapabilities.For(project);
            if (!capabilities.Build || configureOnly && !capabilities.Configure)
            {
                throw new StudioXException((configureOnly ? capabilities.ConfigureError : capabilities.BuildError) ?? "BUILD_UNSUPPORTED",
                    capabilities.Scope + "\n" + capabilities.SourceWorkflow);
            }
            var backend = Backends.SingleOrDefault(backend => backend.Kind == project.Kind)
                ?? throw new StudioXException("BUILD_BACKEND", "工程没有注册构建后端：" + project.Kind);
            return await backend.ExecuteAsync(new(root, project, configureOnly, progress, output, cancellationToken));
        }
        finally { gate.Release(); }
    }
    private sealed record BuildCacheIdentity(string ProjectDirectory, string ToolsetDirectory, string Fingerprint,
        CubeMxProjectSettings? CubeMx = null, int ConfigurationVersion = 0, ProjectBuildSettings? BuildSettings = null,
        string? StcClockConfiguration = null);
}

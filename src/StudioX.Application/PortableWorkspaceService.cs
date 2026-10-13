namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>跨平台桌面的工程、文件与构建用例；切换工程不能与保存、编译或退出交错。</summary>
public sealed class PortableWorkspaceService : IAsyncDisposable
{
    private readonly WorkbenchService workbench;
    private readonly SemaphoreSlim commands = new(1, 1);
    private bool disposed;
    private IReadOnlyList<InstalledPack> packCatalog = [];

    public PortableWorkspaceService(string runtimeDirectory, string dataDirectory)
    {
        workbench = new(runtimeDirectory, dataDirectory);
    }

    public PortableProject? Project
    {
        get; private set;
    }

    public async Task<ThemeDefinition> LoadThemeAsync(CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        return await workbench.Themes.LoadAsync(token).ConfigureAwait(false);
    }

    public async Task SelectThemeAsync(ThemeDefinition theme, CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        await workbench.Themes.SelectAsync(theme, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentProject>> RecentAsync(CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        return await workbench.RecentProjects.LoadAsync(token).ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        Project = null;
    }

    public async Task ImportPackAsync(string path, CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        await workbench.Packs.ImportAsync(path, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PortablePackChoice>> PacksAsync(CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        packCatalog = await workbench.Packs.ListCatalogAsync(token).ConfigureAwait(false);
        return packCatalog.GroupBy(pack => pack.Manifest.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(pack => Version.Parse(pack.Manifest.Version)).First())
            .Select(pack => new PortablePackChoice(pack.Manifest.Id, pack.Manifest.Version, pack.Manifest.DisplayName,
                pack.Manifest.Vendor, pack.ContentHash, pack.Manifest.Devices.Select(device => new PortableDeviceChoice(device.Id, device.DisplayName,
                    device.Templates.Select(template => new PortableTemplateChoice(template.Id, template.DisplayName, template.Description,
                        DevelopmentComponentRequirements.ForTemplate(device, template).All(need => need.Host == HostPlatform.Current))).ToArray())).ToArray()))
            .ToArray();
    }

    public async Task CreateAsync(PortablePackChoice selected, string deviceId, string templateId, string name, string destination,
        CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        var pack = packCatalog.SingleOrDefault(pack => pack.Manifest.Id == selected.Id && pack.Manifest.Version == selected.Version && pack.ContentHash == selected.ContentHash)
            ?? throw new StudioXException("PACK_CHANGED", "器件包选择已过期，请重新选择。");
        var device = pack.Manifest.Devices.SingleOrDefault(device => device.Id == deviceId)
            ?? throw new StudioXException("DEVICE_UNKNOWN", "器件不在选中的包中。");
        var template = device.Templates.SingleOrDefault(template => template.Id == templateId)
            ?? throw new StudioXException("TEMPLATE_UNKNOWN", "模板不在选中的器件中。");
        if (DevelopmentComponentRequirements.ForTemplate(device, template).Any(need => need.Host != HostPlatform.Current))
        {
            throw new StudioXException("TOOLS_HOST", "所选模板没有声明适用于当前平台的开发环境组件。");
        }
        await workbench.Projects.CreateAsync(pack, deviceId, templateId, name, destination, token).ConfigureAwait(false);
    }

    public async Task<PortableProject> OpenAsync(string directory, CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        var root = Path.GetFullPath(directory);
        var manifest = await ProjectService.ReadAsync(root, token).ConfigureAwait(false);
        var entry = manifest.EntryFile ?? (manifest.Kind == ProjectKind.CubeMx ? "Core/Src/main.c" : "src/main.c");
        await workbench.RecentProjects.RememberAsync(manifest.Name, root, token).ConfigureAwait(false);
        Project = new(root, manifest.Name, manifest.DeviceId, manifest.TemplateId,
            manifest.ToolsetId + " / " + manifest.ToolsetVersion, entry);
        return Project;
    }

    public async Task<IReadOnlyList<ProjectEntry>> ListAsync(string relativeDirectory = "", CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        return await workbench.Files.ListAsync(RequireProject().Directory, relativeDirectory, token).ConfigureAwait(false);
    }

    public async Task<PortableDocument> ReadAsync(string relativePath, CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        var root = RequireProject().Directory;
        return new(root, await workbench.Files.ReadAsync(root, relativePath, token).ConfigureAwait(false));
    }

    public async Task<PortableDocument> SaveAsync(PortableDocument document, string text, CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        var root = RequireProject().Directory;
        if (!root.Equals(document.ProjectDirectory, HostPlatform.PathComparison))
        {
            throw new StudioXException("WORKSPACE_DOCUMENT", "文档属于另一个工程，请重新打开后保存。");
        }
        return document with
        {
            Source = await workbench.Files.SaveAsync(root, document.Source, text, token).ConfigureAwait(false)
        };
    }

    public async Task<PortableBuildReport> BuildAsync(IProgress<string>? progress = null, IProgress<string>? output = null,
        CancellationToken token = default)
    {
        using var command = await EnterAsync(token).ConfigureAwait(false);
        var result = await workbench.Builds.BuildAsync(RequireProject().Directory, progress, token, output).ConfigureAwait(false);
        return new(result.Success, result.Summary, result.Log, result.LogPath, result.Artifacts);
    }

    private PortableProject RequireProject() => Project ?? throw new StudioXException("PROJECT_REQUIRED", "请先打开 StudioX 工程。");

    private async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        if (!await commands.WaitAsync(0, token).ConfigureAwait(false))
        {
            throw new StudioXException("WORKSPACE_BUSY", "工程操作正在进行，请等待完成或取消。");
        }
        if (disposed)
        {
            commands.Release();
            throw new ObjectDisposedException(nameof(PortableWorkspaceService));
        }
        return new CommandLease(commands);
    }

    public async ValueTask DisposeAsync()
    {
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            await workbench.DisposeAsync().ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }

    private sealed class CommandLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

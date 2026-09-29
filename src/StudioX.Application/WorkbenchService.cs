namespace StudioX.Application;

using StudioX.Application.CodeIntelligence;
using StudioX.Application.Espressif;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;
using StudioX.Application.Simulation;
using StudioX.Application.Skills;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Packages;

/// <summary>工作台的服务组合根；各服务拥有自己的业务状态和资源生命周期。</summary>
public sealed class WorkbenchService : IAsyncDisposable
{
    public WorkbenchService(string runtimeDirectory, string dataDirectory)
    {
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        LocalHistory = new Editing.LocalHistoryService(DataDirectory);
        Files = new ProjectFileService(LocalHistory);
        WorkspaceDiscovery = new Editing.WorkspaceDiscoveryService(Files);
        WorkbenchLayout = new Editing.WorkbenchLayoutService(DataDirectory);
        Git = new GitRepositoryService(RuntimeDirectory);
        GitGraph = new GitGraphService(Git);
        GitHubAuthentication = new GitHubAuthenticationService(Git);
        GitHubProfiles = new GitHubProfileService(GitHubAuthentication);
        GitHubAccounts = new GitHubAccountService(GitHubAuthentication, GitHubProfiles);
        GitHubPullRequests = new GitHubPullRequestService(GitHubAuthentication);
        Projects = new ProjectService(Git.InitializeAsync);
        ZephyrProjects = new ZephyrProjectService(Git.InitializeAsync);
        Terminal = new Terminal.ProjectTerminalService(Git);
        Packs = new PackRepository(Path.Combine(DataDirectory, "packs"));
        ZephyrPacks = new ZephyrPackRepository(Path.Combine(DataDirectory, "zephyr-packs"));
        RemotePacks = new GitHubPackSyncService(Packs);
        Toolsets = new ToolsetCatalog(Path.Combine(RuntimeDirectory, "toolsets"));
        ToolInventory = new ToolInventoryService(Toolsets);
        ToolEnvironment = new ToolEnvironmentService(Toolsets);
        Builds = new BuildService(Toolsets, Path.Combine(DataDirectory, "licenses", "ag32-pin-mapping"));
        BuildMemory = new BuildMemoryService(Toolsets);
        LvglPreview = new Lvgl.LvglPreviewService(Toolsets, DataDirectory);
        Ag32Logic = new Ag32LogicWorkflowService();
        HdlSchematic = new HdlSchematicService(RuntimeDirectory);
        HdlWorkflow = new HdlWorkflowService(Toolsets, Path.Combine(DataDirectory, "licenses", "ag32-pin-mapping"));
        Ag32PinMapping = new Ag32PinMappingBuildService(Toolsets, Path.Combine(DataDirectory, "licenses", "ag32-pin-mapping"));
        Ag32PinPlanning = new Ag32PinPlanningService(Toolsets);
        CubeMx = new CubeMxImportService(Toolsets);
        Downloads = new OpenOcdService(Toolsets);
        EspressifDownloads = new EspressifDownloadService(new EspressifFlashService(Toolsets), Devices);
        StcIsp = new StcIspService(Toolsets, RuntimeDirectory, DataDirectory);
        Debugger = new DebugSessionService(DataDirectory);
        OpenOcdPlot = new OpenOcdPlot.OpenOcdPlotService(Debugger);
        Debugger.Changed += OpenOcdPlot.StopIfSessionEnded;
        Themes = new ThemeService(DataDirectory);
        Appearance = new AppearanceService(DataDirectory);
        RecentProjects = new RecentProjectService(DataDirectory);
        EditorSettings = new EditorSettingsService(DataDirectory);
        EditorSessions = new Editing.EditorSessionStore(DataDirectory);
        WorkspaceEdits = new Editing.WorkspaceEditService(Files);
        AiSettings = new AiSettingsService(DataDirectory);
        AiSkills = new AiSkillService(DataDirectory, RuntimeDirectory);
        AiConversations = new AiConversationStore(DataDirectory);
        AiCredentials = new AiCredentialStore();
        WebCredentials = new WebCredentialStore();
        WebResearch = new WebResearchService(apiKeyProvider: () => WebCredentials.GetApiKey() ?? Environment.GetEnvironmentVariable("TAVILY_API_KEY"));
        EspressifDocumentation = new EspressifDocumentationService(DataDirectory);
        EspressifComponents = new EspressifComponentRegistryService();
        AiChat = new AiChatClient(AiCredentials);
        Intelligence = new CodeIntelligenceService(RuntimeDirectory, DataDirectory);
        Serial = new Serial.SerialTerminalService(Devices, DataDirectory, scriptHostExecutable: Path.Combine(RuntimeDirectory, "plugin-host", "StudioX.PluginHost.exe"));
        MicroPython = new MicroPython.MicroPythonSessionService(Devices);
        SerialPlot = new SerialPlot.SerialPlotService(Devices, DataDirectory);
        Plugins = new PluginClient(Path.Combine(RuntimeDirectory, "plugin-host", "StudioX.PluginHost.exe"));
        PluginManager = new PluginManagerService(RuntimeDirectory, DataDirectory);
        PluginSettings = new PluginSettingsService(DataDirectory);
        Simulation = new SimulationLabService(Devices);
    }
    public string RuntimeDirectory
    {
        get;
    }
    public PluginSettingsService PluginSettings
    {
        get;
    }
    public Editing.EditorSessionStore EditorSessions
    {
        get;
    }
    public Editing.WorkspaceEditService WorkspaceEdits
    {
        get;
    }
    public HdlSchematicService HdlSchematic
    {
        get;
    }
    public HdlWorkflowService HdlWorkflow
    {
        get;
    }
    public string DataDirectory
    {
        get;
    }
    public PackRepository Packs
    {
        get;
    }
    public ZephyrPackRepository ZephyrPacks
    {
        get;
    }
    public ZephyrProjectService ZephyrProjects
    {
        get;
    }
    public GitHubPackSyncService RemotePacks
    {
        get;
    }
    public ToolsetCatalog Toolsets
    {
        get;
    }
    public ToolInventoryService ToolInventory
    {
        get;
    }
    public GitRepositoryService Git
    {
        get;
    }
    public GitGraphService GitGraph
    {
        get;
    }
    public GitHubAuthenticationService GitHubAuthentication
    {
        get;
    }
    public GitHubProfileService GitHubProfiles
    {
        get;
    }
    public GitHubAccountService GitHubAccounts
    {
        get;
    }
    public GitHubPullRequestService GitHubPullRequests
    {
        get;
    }
    public ProjectService Projects
    {
        get;
    }
    public Terminal.ProjectTerminalService Terminal
    {
        get;
    }
    public BuildService Builds
    {
        get;
    }
    public Lvgl.LvglPreviewService LvglPreview
    {
        get;
    }
    public Ag32LogicWorkflowService Ag32Logic
    {
        get;
    }
    public BuildMemoryService BuildMemory
    {
        get;
    }
    public Ag32PinMappingBuildService Ag32PinMapping
    {
        get;
    }
    public Ag32PinPlanningService Ag32PinPlanning
    {
        get;
    }
    public CubeMxImportService CubeMx
    {
        get;
    }
    public OpenOcdService Downloads
    {
        get;
    }
    public StcIspService StcIsp
    {
        get;
    }
    public EspressifDownloadService EspressifDownloads
    {
        get;
    }
    public DebugSessionService Debugger
    {
        get;
    }
    public OpenOcdPlot.OpenOcdPlotService OpenOcdPlot { get; }
    public DeviceHub Devices { get; } = new();
    public SimulationLabService Simulation
    {
        get;
    }
    public Serial.SerialTerminalService Serial
    {
        get;
    }
    public SerialPlot.SerialPlotService SerialPlot
    {
        get;
    }
    public ThemeService Themes
    {
        get;
    }
    public AppearanceService Appearance
    {
        get;
    }
    public RecentProjectService RecentProjects
    {
        get;
    }
    public ProjectFileService Files
    {
        get;
    }
    public Editing.LocalHistoryService LocalHistory
    {
        get;
    }
    public Editing.WorkspaceDiscoveryService WorkspaceDiscovery
    {
        get;
    }
    public Editing.WorkbenchLayoutService WorkbenchLayout
    {
        get;
    }
    public ToolEnvironmentService ToolEnvironment
    {
        get;
    }
    public EditorSettingsService EditorSettings
    {
        get;
    }
    public AiSettingsService AiSettings
    {
        get;
    }
    public AiSkillService AiSkills
    {
        get;
    }
    public AiConversationStore AiConversations
    {
        get;
    }
    public AiCredentialStore AiCredentials
    {
        get;
    }
    public WebCredentialStore WebCredentials
    {
        get;
    }
    public WebResearchService WebResearch
    {
        get;
    }
    public EspressifDocumentationService EspressifDocumentation
    {
        get;
    }
    public EspressifComponentRegistryService EspressifComponents
    {
        get;
    }
    public AiChatClient AiChat
    {
        get;
    }
    public AiAgentService CreateAiAgent(AiSettings settings, StudioXMcpSession mcpSession) =>
        new(AiChat, settings, mcpSession);
    public CodeIntelligenceService Intelligence
    {
        get;
    }
    public CMakeAssistanceService CMake { get; } = new();
    public PythonAssistanceService Python { get; } = new();
    public PythonNavigationService PythonNavigation { get; } = new();
    public MicroPython.MicroPythonSessionService MicroPython
    {
        get;
    }
    public PluginClient Plugins
    {
        get;
    }
    public PluginManagerService PluginManager
    {
        get;
    }
    public IEnumerable<string> PluginManifests => Directory.Exists(Path.Combine(RuntimeDirectory, "plugins"))
        ? Directory.EnumerateFiles(Path.Combine(RuntimeDirectory, "plugins"), "plugin.json", SearchOption.AllDirectories) : [];
    public static Task<string> ReadMainAsync(string project, CancellationToken token = default) => File.ReadAllTextAsync(Path.Combine(project, "src", "main.c"), token);
    public static Task SaveMainAsync(string project, string text, CancellationToken token = default) => File.WriteAllTextAsync(Path.Combine(project, "src", "main.c"), text, token);
    public async ValueTask DisposeAsync()
    {
        var cleanup = new ResourceCleanup();
        cleanup.Run(EditorSessions.Dispose);
        await cleanup.RunAsync(PluginManager.DisposeAsync);
        cleanup.Run(AiChat.Dispose);
        cleanup.Run(RemotePacks.Dispose);
        cleanup.Run(GitHubPullRequests.Dispose);
        cleanup.Run(GitHubProfiles.Dispose);
        await cleanup.RunAsync(EspressifDocumentation.DisposeAsync);
        await cleanup.RunAsync(EspressifComponents.DisposeAsync);
        await cleanup.RunAsync(LvglPreview.DisposeAsync);
        await cleanup.RunAsync(Terminal.DisposeAsync);
        await cleanup.RunAsync(Simulation.DisposeAsync);
        await cleanup.RunAsync(SerialPlot.DisposeAsync);
        await cleanup.RunAsync(Serial.DisposeAsync);
        await cleanup.RunAsync(MicroPython.DisposeAsync);
        await cleanup.RunAsync(OpenOcdPlot.DisposeAsync);
        await cleanup.RunAsync(Debugger.DisposeAsync);
        await cleanup.RunAsync(Intelligence.DisposeAsync);
        // 先释放各用例自己的会话，再关闭所有设备，避免观察者收到已释放对象。
        await cleanup.RunAsync(Devices.DisposeAsync);
        cleanup.ThrowIfFailed("工作台资源清理失败。");
    }
}

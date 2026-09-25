namespace StudioX.Application;

using StudioX.Devices;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Packages;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Mcp;
using StudioX.Application.Skills;

public sealed class WorkbenchService : IAsyncDisposable
{
    public WorkbenchService(string runtimeDirectory, string dataDirectory)
    {
        RuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        Git = new GitRepositoryService(RuntimeDirectory);
        GitGraph = new GitGraphService(Git);
        GitHubAuthentication = new GitHubAuthenticationService(Git);
        GitHubProfiles = new GitHubProfileService(GitHubAuthentication);
        GitHubAccounts = new GitHubAccountService(GitHubAuthentication, GitHubProfiles);
        GitHubPullRequests = new GitHubPullRequestService(GitHubAuthentication);
        Projects = new ProjectService(Git.InitializeAsync);
        Terminal = new Terminal.ProjectTerminalService(Git);
        Packs = new PackRepository(Path.Combine(DataDirectory, "packs"));
        RemotePacks = new GitHubPackSyncService(Packs);
        Toolsets = new ToolsetCatalog(Path.Combine(RuntimeDirectory, "toolsets"));
        ToolInventory = new ToolInventoryService(Toolsets);
        Builds = new BuildService(Toolsets);
        Ag32Logic = new Ag32LogicWorkflowService();
        CubeMx = new CubeMxImportService(Toolsets);
        Downloads = new OpenOcdService(Toolsets);
        StcIsp = new StcIspService(Toolsets, RuntimeDirectory, DataDirectory);
        Debugger = new DebugSessionService(DataDirectory);
        Themes = new ThemeService(DataDirectory);
        Appearance = new AppearanceService(DataDirectory);
        RecentProjects = new RecentProjectService(DataDirectory);
        EditorSettings = new EditorSettingsService(DataDirectory);
        AiSettings = new AiSettingsService(DataDirectory);
        AiSkills = new AiSkillService(DataDirectory, RuntimeDirectory);
        AiConversations = new AiConversationStore(DataDirectory);
        AiCredentials = new AiCredentialStore();
        WebCredentials = new WebCredentialStore();
        WebResearch = new WebResearchService(apiKeyProvider: () => WebCredentials.GetApiKey() ?? Environment.GetEnvironmentVariable("TAVILY_API_KEY"));
        AiChat = new AiChatClient(AiCredentials);
        Intelligence = new CodeIntelligenceService(RuntimeDirectory, DataDirectory);
        Serial = new Serial.SerialTerminalService(Devices, DataDirectory, scriptHostExecutable: Path.Combine(RuntimeDirectory, "plugin-host", "StudioX.PluginHost.exe"));
        SerialPlot = new SerialPlot.SerialPlotService(Devices, DataDirectory);
        Plugins = new PluginClient(Path.Combine(RuntimeDirectory, "plugin-host", "StudioX.PluginHost.exe"));
    }
    public string RuntimeDirectory { get; }
    public string DataDirectory { get; }
    public PackRepository Packs { get; }
    public GitHubPackSyncService RemotePacks { get; }
    public ToolsetCatalog Toolsets { get; }
    public ToolInventoryService ToolInventory { get; }
    public GitRepositoryService Git { get; }
    public GitGraphService GitGraph { get; }
    public GitHubAuthenticationService GitHubAuthentication { get; }
    public GitHubProfileService GitHubProfiles { get; }
    public GitHubAccountService GitHubAccounts { get; }
    public GitHubPullRequestService GitHubPullRequests { get; }
    public ProjectService Projects { get; }
    public Terminal.ProjectTerminalService Terminal { get; }
    public BuildService Builds { get; }
    public Ag32LogicWorkflowService Ag32Logic { get; }
    public BuildMemoryService BuildMemory { get; } = new();
    public CubeMxImportService CubeMx { get; }
    public OpenOcdService Downloads { get; }
    public StcIspService StcIsp { get; }
    public DebugSessionService Debugger { get; }
    public DeviceHub Devices { get; } = new();
    public Serial.SerialTerminalService Serial { get; }
    public SerialPlot.SerialPlotService SerialPlot { get; }
    public ThemeService Themes { get; }
    public AppearanceService Appearance { get; }
    public RecentProjectService RecentProjects { get; }
    public ProjectFileService Files { get; } = new();
    public EditorSettingsService EditorSettings { get; }
    public AiSettingsService AiSettings { get; }
    public AiSkillService AiSkills { get; }
    public AiConversationStore AiConversations { get; }
    public AiCredentialStore AiCredentials { get; }
    public WebCredentialStore WebCredentials { get; }
    public WebResearchService WebResearch { get; }
    public AiChatClient AiChat { get; }
    public AiAgentService CreateAiAgent(AiSettings settings, StudioXMcpSession mcpSession) =>
        new(AiChat, settings, mcpSession);
    public CodeIntelligenceService Intelligence { get; }
    public CMakeAssistanceService CMake { get; } = new();
    public PluginClient Plugins { get; }
    public IEnumerable<string> PluginManifests => Directory.Exists(Path.Combine(RuntimeDirectory, "plugins"))
        ? Directory.EnumerateFiles(Path.Combine(RuntimeDirectory, "plugins"), "plugin.json", SearchOption.AllDirectories) : [];
    public static Task<string> ReadMainAsync(string project, CancellationToken token = default) => File.ReadAllTextAsync(Path.Combine(project, "src", "main.c"), token);
    public static Task SaveMainAsync(string project, string text, CancellationToken token = default) => File.WriteAllTextAsync(Path.Combine(project, "src", "main.c"), text, token);
    public async ValueTask DisposeAsync() { AiChat.Dispose(); RemotePacks.Dispose(); GitHubPullRequests.Dispose(); GitHubProfiles.Dispose(); await Terminal.DisposeAsync(); await SerialPlot.DisposeAsync(); await Serial.DisposeAsync(); await Debugger.DisposeAsync(); await Intelligence.DisposeAsync(); await Devices.DisposeAsync(); }
}

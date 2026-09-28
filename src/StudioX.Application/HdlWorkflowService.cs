namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Engine.Hdl;

/// <summary>联合构建配置与 RTL 仿真用例，供桌面及 Agent 共用。</summary>
public sealed class HdlWorkflowService(ToolsetCatalog catalog, string licenseDirectory)
{
    private readonly Ag32NativeBuildService build = new(catalog, licenseDirectory);
    private readonly HdlSimulationEngine simulation = new(catalog);
    public Task<Ag32NativeBuildSettings> ReadBuildAsync(string root, CancellationToken token = default) => build.ReadSettingsAsync(root, token);
    public Task SaveBuildAsync(string root, Ag32NativeBuildSettings settings, CancellationToken token = default) => build.SaveSettingsAsync(root, settings, token);
    public Task<HdlSimulationSettings> ReadSimulationAsync(string root, CancellationToken token = default) => simulation.ReadSettingsAsync(root, token);
    public Task SaveSimulationAsync(string root, HdlSimulationSettings settings, CancellationToken token = default) => simulation.SaveSettingsAsync(root, settings, token);
    public Task<HdlSimulationResult> SimulateAsync(string root, HdlSimulationSettings settings, CancellationToken token = default) => simulation.RunAsync(root, settings, token);
    public Task CreateTestbenchAsync(string root, string relative, string top, CancellationToken token = default) => simulation.CreateTestbenchAsync(root, relative, top, token);
    public Task<bool> IsCurrentAsync(HdlSimulationResult result, CancellationToken token = default) => simulation.IsCurrentAsync(result, token);
    public Task<string> ReportPathAsync(string root, string report, CancellationToken token = default) => build.ReportPathAsync(root, report, token);
}

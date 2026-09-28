namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

/// <summary>Agent 与界面共用联合构建配置及事件仿真，不隐式连接硬件。</summary>
internal sealed class HdlWorkflowMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "ag32_logic_workflow_settings")]
    [Description("读取 AG32 自定义逻辑构建与 RTL 仿真配置。可提供 build_json 或 simulation_json 保存完整配置（需要 FileWrite 授权）。源码、包含目录和 SDC 均为工程相对路径。project_build 会联合编译 MCU 与 FPGA；配置保存本身不构建或下载。")]
    public async Task<string> SettingsAsync(string? build_json = null, string? simulation_json = null, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken);
        if (build_json is not null || simulation_json is not null)
        {
            await RequireSavedDocumentsAsync();
            await RequireApprovalAsync("ag32_logic_workflow_settings", "保存当前工程的逻辑构建或仿真配置。", StudioXMcpPermission.FileWrite, cancellationToken);
            await RequireSavedDocumentsAsync();
            if (build_json is not null)
                await Services.HdlWorkflow.SaveBuildAsync(Project, JsonSerializer.Deserialize<Ag32NativeBuildSettings>(build_json, JsonStore.Options)
                    ?? throw new ArgumentException("构建配置为空。"), cancellationToken);
            if (simulation_json is not null)
                await Services.HdlWorkflow.SaveSimulationAsync(Project, JsonSerializer.Deserialize<HdlSimulationSettings>(simulation_json, JsonStore.Options)
                    ?? throw new ArgumentException("仿真配置为空。"), cancellationToken);
        }
        return JsonSerializer.Serialize(new { build = await Services.HdlWorkflow.ReadBuildAsync(Project, cancellationToken),
            simulation = await Services.HdlWorkflow.ReadSimulationAsync(Project, cancellationToken) });
    }

    [McpServerTool(Name = "ag32_logic_simulate")]
    [Description("使用内置 Icarus Verilog 运行工程配置的 RTL testbench，返回 VCD、原始日志、时间单位及信号列表。支持 #delay 和数字 X/Z；不是硬件采样或带 SDF 的布局后时序仿真。需要 Build 授权，不下载。断言失败、超时、输入变化均报错。")]
    public async Task<string> SimulateAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken);
        await RequireSavedDocumentsAsync();
        await RequireApprovalAsync("ag32_logic_simulate", "运行 RTL testbench 并生成本地 VCD 波形，不连接硬件。", StudioXMcpPermission.Build, cancellationToken);
        await RequireSavedDocumentsAsync();
        var settings = await Services.HdlWorkflow.ReadSimulationAsync(Project, cancellationToken);
        var result = await Services.HdlWorkflow.SimulateAsync(Project, settings, cancellationToken);
        return JsonSerializer.Serialize(new { mode = "RTL", result.VcdPath, result.LogPath, result.Warnings,
            result.Waveform.NanosecondsPerTick, result.Waveform.EndTick,
            signals = result.Waveform.Signals.Select(signal => new { signal.Name, signal.Width, changes = signal.Changes.Length }) });
    }
}

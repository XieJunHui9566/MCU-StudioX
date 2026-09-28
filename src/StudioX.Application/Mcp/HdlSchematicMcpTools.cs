namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>复用桌面综合服务提供 AG32 RTL 网表与电路图，不涉及 FPGA 下载。</summary>
internal sealed class HdlSchematicMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "ag32_logic_schematic_settings")]
    [Description("读取当前 AG32 MCU+FPGA 工程的 Verilog 电路预览配置：源文件、包含目录、宏、顶层模块和是否展开子模块。只读，不执行综合或连接硬件。")]
    public async Task<string> SettingsAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.HdlSchematic.ReadSettingsAsync(Project, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "ag32_logic_schematic_generate")]
    [Description("授权构建后用内置 Yosys 综合当前 AG32 自定义 Verilog，生成 RTL 网表、SVG 电路图和原始日志。使用工程的 hdl-schematic.json 或自动发现配置；可覆盖顶层模块及展开选择。仅生成预览，不代表 FPGA 布局布线、时序或硬件验收，不下载。返回所有模块及元件统计和工件路径，详细端口与信号连接可读取 netlist.json。")]
    public async Task<string> GenerateAsync(string? top_module = null, bool? flatten = null, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var settings = await Services.HdlSchematic.ReadSettingsAsync(Project, cancellationToken).ConfigureAwait(false);
        settings = settings with
        {
            TopModule = top_module ?? settings.TopModule,
            Flatten = flatten ?? settings.Flatten
        };
        await RequireApprovalAsync("ag32_logic_schematic_generate", "综合当前 Verilog 为 RTL 网表，在工程 .build/hdl-schematic 下生成电路图和日志，不连接硬件。",
            StudioXMcpPermission.Build, cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var result = await Services.HdlSchematic.GenerateAsync(Project, settings, cancellationToken).ConfigureAwait(false);
        var diagram = Services.HdlSchematic.CreateDiagram(result.Modules.Single(module => module.IsTop));
        var svg = Path.Combine(Path.GetDirectoryName(result.NetlistPath)!, "schematic.svg");
        await Services.HdlSchematic.ExportSvgAsync(diagram, svg, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            result.TopModule,
            result.SourceDigest,
            result.ToolVersion,
            result.NetlistPath,
            result.LogPath,
            result.Warnings,
            svg,
            modules = result.Modules.Select(module => new { module.Name, module.IsBlackBox, ports = module.Ports, cells = module.Cells.Length }),
        });
    }
}

namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>管理 AG32 基础 VE 映射的显式启用和离线编译；下载统一使用固件双镜像计划。</summary>
internal sealed class Ag32PinMappingMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "ag32_pin_mapping_status")]
    [Description("读取 AG32 基础 .ve 映射状态、源文件、逻辑镜像及构建凭据是否仍有效；不编译、不连接硬件。自定义 Verilog 模式返回外部综合诊断。")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.Ag32PinMapping.InspectAsync(Project, cancellationToken)
            .ConfigureAwait(false));
    }

    [McpServerTool(Name = "ag32_pin_mapping_enable")]
    [Description("逐次授权后为已适配的 AGM AG32 工程启用基础 .ve 映射；按准确型号选择逻辑目标，保留已有 logic/pins.ve，缺失时建立模板。不把自定义 Verilog 工程转换为基础映射，不烧录。")]
    public async Task<string> EnableAsync(CancellationToken cancellationToken = default)
    {
        var project = await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (project.PinMapping is not null && project.Logic is null &&
            File.Exists(PathBoundary.Resolve(Project, project.PinMapping.PinMapFile)))
        {
            return JsonSerializer.Serialize(new
            {
                enabled = true,
                path = project.PinMapping.PinMapFile,
                changed = false
            });
        }
        if (Ag32DeviceCatalog.Find(project.DeviceId)?.CanMap != true || project.Logic is not null)
        {
            throw new StudioXException("AG32_MAPPING_DEVICE", "基础引脚映射仅适用于已适配 AG32 的默认逻辑工程；自定义 Verilog 不会被自动转换。");
        }
        var manifestPath = PathBoundary.Resolve(Project, ".studiox/project.json");
        var vePath = PathBoundary.Resolve(Project, "logic/pins.ve");
        var manifestHash = await HashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var veHash = await HashAsync(vePath, cancellationToken).ConfigureAwait(false);
        await RequireApprovalAsync("ag32_pin_mapping_enable",
            $"启用当前 {project.DeviceId} 工程的基础引脚映射，更新工程清单；"
                + (veHash is null ? "建立 logic/pins.ve 模板。" : $"保留 logic/pins.ve（SHA-256 {veHash}）。")
                + "之后普通编译同时生成 MCU 固件和映射镜像，下载仍须单独确认两份镜像。",
            StudioXMcpPermission.FileWrite, cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (manifestHash != await HashAsync(manifestPath, cancellationToken).ConfigureAwait(false) ||
            veHash != await HashAsync(vePath, cancellationToken).ConfigureAwait(false))
        {
            throw new StudioXException("AG32_MAPPING_CHANGED", "审批期间工程清单或 VE 文件已变化，请重新启用。");
        }
        var enabled = await ProjectService.EnableAg32PinMappingAsync(Project, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            enabled = true,
            path = enabled.PinMapping!.PinMapFile,
            changed = true
        });
    }

    [McpServerTool(Name = "ag32_pin_plan_read")]
    [Description("读取当前 AG32 图形引脚规划：准确封装的全部引脚、厂商可映射功能及复用资源、VE 映射、显式 HSECLK/SYSCLK/BUSCLK（MHz）和源文件 SHA-256。复杂 VE 返回只读诊断。不连接硬件，不改文件。")]
    public async Task<string> ReadPlanAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.Ag32PinPlanning.ReadAsync(Project, cancellationToken)
            .ConfigureAwait(false), JsonStore.Options);
    }

    [McpServerTool(Name = "ag32_pin_plan_apply")]
    [Description("逐次授权后按当前源 SHA-256 保存 AG32 图形规划并生成实际厂商 VEX/SDC 约束。assignments_json 为完整映射数组，例如 [{\"function\":\"GPIO4_4\",\"pinNumber\":21}]；时钟单位 MHz，null 表示未显式指定。校验引脚、功能复用及厂商转换，保留注释和未知配置行，失败不覆盖 VE。不运行 Supra，不烧录；正式映射镜像仍由 project_build 生成。")]
    public async Task<string> ApplyPlanAsync(string expected_source_sha256, string assignments_json,
        decimal? hse_mhz = null, decimal? sys_mhz = null, decimal? bus_mhz = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (assignments_json.Length > 64 * 1024)
        {
            throw new StudioXException("AG32_PIN_PLAN_INPUT", "引脚规划 JSON 超过 64 KiB，无法解析。");
        }
        Ag32PinAssignment[] assignments;
        try
        {
            assignments = JsonSerializer.Deserialize<Ag32PinAssignment[]>(assignments_json, JsonStore.Options)
                ?? throw new StudioXException("AG32_PIN_PLAN_INPUT", "引脚规划需要完整的映射数组。");
        }
        catch (JsonException error)
        {
            throw new StudioXException("AG32_PIN_PLAN_INPUT", "引脚规划 JSON 无效：" + error.Message, error);
        }
        if (assignments.Any(item => item is null || string.IsNullOrWhiteSpace(item.Function)))
        {
            throw new StudioXException("AG32_PIN_PLAN_INPUT", "每项引脚分配都需要有效的内部功能名称，数组不能包含 null。");
        }
        var manifestPath = PathBoundary.Resolve(Project, ".studiox/project.json");
        var manifestHash = await HashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var snapshot = await Services.Ag32PinPlanning.ReadAsync(Project, cancellationToken).ConfigureAwait(false);
        var summary = $"保存 {snapshot.DeviceId} 的 {assignments.Length} 项引脚分配到 {snapshot.SourcePath}；"
            + $"当前 VE SHA-256：{expected_source_sha256}。\n"
            + string.Join("\n", assignments.Select(item => $"{item.Function} → PIN_{item.PinNumber}"))
            + $"\nHSECLK={hse_mhz?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未显式配置"} MHz，"
            + $"SYSCLK={sys_mhz?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未显式配置"} MHz，"
            + $"BUSCLK={bus_mhz?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未显式配置"} MHz。"
            + "\n先校验厂商转换，再保存并生成约束；不会连接或写入芯片。";
        await RequireApprovalAsync("ag32_pin_plan_apply", summary, StudioXMcpPermission.FileWrite, cancellationToken)
            .ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (manifestHash != await HashAsync(manifestPath, cancellationToken).ConfigureAwait(false) ||
            !string.Equals(expected_source_sha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("AG32_PIN_PLAN_STALE", "审批期间工程目标已变化，或请求不是当前 VE 的散列，请重新读取图形规划。");
        }
        var result = await Services.Ag32PinPlanning.ApplyAsync(Project, snapshot,
            assignments, new(hse_mhz, sys_mhz, bus_mhz), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            applied = true,
            path = result.Snapshot.SourcePath,
            result.Snapshot,
            result.VexPath,
            result.SdcPath,
            converterDiagnostics = LimitOutput(result.ConverterDiagnostics),
            hardwareConnected = false
        }, JsonStore.Options);
    }

    [McpServerTool(Name = "ag32_pin_mapping_build")]
    [Description("逐次授权后使用内置 AGM VE/Supra 工具编译当前基础 .ve 映射并生成逻辑 BIN；无需 Quartus，需要本机有效 Supra 厂商许可。保留原始诊断，不连接硬件。普通 project_build 也会联动编译映射。")]
    public async Task<string> BuildAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("ag32_pin_mapping_build", "用内置 AGM 工具编译当前基础 VE 映射并建立源码、工具和镜像哈希凭据；不下载或连接硬件。",
            StudioXMcpPermission.Build, cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var report = await Services.Ag32PinMapping.BuildAsync(Project, cancellationToken: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            report.Success,
            report.Artifacts,
            report.LogPath,
            report.ExitCode,
            report.TimedOut,
            log = LimitOutput(report.Log, 12_000),
            next = "映射不会因编译自动写入芯片；使用 firmware_download_plan 核对 MCU 与映射两个镜像，再明确授权 firmware_download。"
        });
    }

    private static async Task<string?> HashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}

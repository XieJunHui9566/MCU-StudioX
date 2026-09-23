namespace StudioX.Engine.Debugging;

using System.Globalization;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record OpenOcdDebugPlan(string OpenOcd, string[] OpenOcdArguments, string Gdb, string[] GdbArguments, string[] InitializeCommands);

/// <summary>器件包声明的 OpenOCD 会话；附加并核对固件，不隐式下载。</summary>
public static class OpenOcdDebugPlanner
{
    public static DebugProbeDefinition ResolveProbe(DownloadConfiguration configuration)
    {
        var target = ResolveTarget(configuration);
        if (target.IsRp2350 && (configuration.Options.ProbeId != "cmsis-dap" || configuration.OpenOcd.Probes.Count != 1))
            throw new StudioXException("DEBUG_PROBE", "RP2350 当前使用 DAP-Link (CMSIS-DAP) 调试。");
        if (target.IsRp2350 && (configuration.TargetScriptText is not null ||
            configuration.OpenOcd.TargetScript != configuration.Device.OpenOcd!.TargetScript ||
            configuration.OpenOcd.ApplicationFlashBytes != configuration.Device.OpenOcd.ApplicationFlashBytes))
            throw new StudioXException("DEBUG_CONFIG", "RP2350 调试需要器件包内匹配的板型与 Flash 配置。");
        if (target.IsAg32 && (configuration.Options.ProbeId != "agm-blaster" || configuration.OpenOcd.Probes.Count != 1))
            throw new StudioXException("DEBUG_PROBE", "AG32VF303 当前仅适配官方 AGM BLASTER 的 DAP-Link 模式。");
        if (target.IsWch && (configuration.Options.ProbeId != "wch-link" || configuration.OpenOcd.Probes.Count != 1))
            throw new StudioXException("DEBUG_PROBE", "当前 WCH 器件使用 WCH-Link / WCH-LinkE 的 RISC-V / SDI 模式调试。");
        if (!target.IsAg32 && !target.IsWch && configuration.Options.ProbeId is not ("stlink" or "cmsis-dap"))
            throw new StudioXException("DEBUG_PROBE", "请在烧录器下拉框选择 ST-Link 或 DAP-Link (CMSIS-DAP) 进行调试。");
        var probe = configuration.OpenOcd.Probes.SingleOrDefault(p => p.Id == configuration.Options.ProbeId)
            ?? throw new StudioXException("DEBUG_PROBE", "当前器件未提供所选烧录器的调试配置。");
        if (target.IsWch)
        {
            if (probe.Transport != "sdi" || probe.InterfaceScript != "interface/wch-link.cfg" || configuration.TargetScriptText is not null ||
                configuration.OpenOcd.TargetScript != configuration.Device.OpenOcd!.TargetScript ||
                configuration.OpenOcd.ApplicationFlashBytes != configuration.Device.OpenOcd.ApplicationFlashBytes)
                throw new StudioXException("DEBUG_CONFIG", "WCH 调试需要器件包内匹配的 WCH-Link SDI 配置。");
            return probe with { DisplayName = "WCH-Link / WCH-LinkE" };
        }
        if (probe.Transport != "swd" || probe.InterfaceScript != $"interface/{(target.IsAg32 ? "cmsis-dap" : probe.Id)}.cfg")
            throw new StudioXException("DEBUG_CONFIG", "当前调试需要所选烧录器对应的 OpenOCD 接口脚本及 SWD 配置。");
        return probe with { DisplayName = target.IsAg32 ? "AGM BLASTER（官方）" : probe.Id == "cmsis-dap" ? "DAP-Link (CMSIS-DAP)" : "ST-Link" };
    }

    public static DebugTargetProfile ResolveTarget(DownloadConfiguration configuration) =>
        DebugTargetProfile.Find(configuration.Device) ?? throw new StudioXException("DEBUG_TARGET",
            "当前调试支持已收录的 STM32F1/F4、AG32VF303、CH32V203 / V307、CH592 / CH595 和 RP2350；需要对应新版器件包、工具集和存储布局。当前器件：" + configuration.Device.Id);

    public static OpenOcdDebugPlan Create(string project, DownloadConfiguration configuration, ResolvedToolset tools, string elf, int port = 3333)
    {
        var probe = ResolveProbe(configuration);
        var profile = ResolveTarget(configuration);
        if (port is < 1024 or > 65535 || configuration.Options.SpeedKhz is < 100 or > 15000) throw new StudioXException("DEBUG_OPTIONS", "调试端口或接口速度不合法。");
        if (profile.IsWch && (configuration.Options.SpeedKhz is not (400 or 4000 or 6000) || !string.IsNullOrWhiteSpace(configuration.Options.Serial)))
            throw new StudioXException("DEBUG_OPTIONS", "WCH-Link 支持 400、4000、6000 kHz；当前仅支持单台连接，请留空序列号。");
        var scripts = tools.ResourceDirectory("openocdScripts");
        var interfaceFile = PathBoundary.Resolve(scripts, probe.InterfaceScript);
        if (profile.IsWch) interfaceFile = PathBoundary.Resolve(project, "device/" + probe.InterfaceScript);
        var target = configuration.TargetScriptText is null ? PathBoundary.Resolve(project, "device/" + configuration.OpenOcd.TargetScript) : null;
        if (!File.Exists(interfaceFile) || target is not null && !File.Exists(target)) throw new StudioXException("DEBUG_CONFIG", "缺少 OpenOCD 目标配置。");
        var openocd = new List<string> { "-s", scripts, "-c", "bindto 127.0.0.1", "-c", (profile.IsWch ? "gdb_port " : "gdb port ") + port.ToString(CultureInfo.InvariantCulture), "-c", profile.IsWch ? "tcl_port disabled" : "tcl port disabled", "-c", profile.IsWch ? "telnet_port disabled" : "telnet port disabled", "-f", interfaceFile, "-c", "transport select " + probe.Transport };
        // CMSIS-DAP 保留 OpenOCD 默认 auto 后端，兼容 v2 USB bulk 与 v1 HID；不固定 USB VID/PID。
        if (configuration.Options.Serial is { Length: > 0 } serial)
        {
            if (serial.Length > 100 || serial.Any(char.IsControl)) throw new StudioXException("DEBUG_OPTIONS", "烧录器序列号不合法。");
            openocd.AddRange(["-c", "adapter serial " + TclQuote(serial)]);
        }
        openocd.AddRange(target is null ? ["-c", configuration.TargetScriptText!] : ["-f", target]);
        openocd.AddRange(["-c", "adapter speed " + configuration.Options.SpeedKhz.ToString(CultureInfo.InvariantCulture)]);
        openocd.AddRange(["-c", profile.IsWch ? "gdb_flash_program disable" : "gdb flash_program disable"]);
        // 附加正在运行的程序时，CRC 工作区会覆盖 SRAM 全局变量。
        // 调试校验走主机读回比较，不向目标 RAM 放置校验算法。
        // 沁恒脚本的 TAP 为 wch_riscv.cpu，实际 target 名称还带 .0。
        var targetName = profile.IsWch ? "$_TARGETNAME.0" : "$_TARGETNAME";
        // RP2350 的 GDB 内存映射会先探测 QSPI，须暂时保留带备份的工作区；完成身份检查后再禁用。
        openocd.AddRange(["-c", targetName + (profile.IsRp2350 ? " configure -work-area-backup 1" : " configure -work-area-size 0 -work-area-backup 1")]);
        // detach 的 GDB 成功响应不等同于目标已运行；由 OpenOCD 明确检查并回报。
        var detach = profile.IsAg32 ? "studiox_ag32_detach; " : profile.IsWch ? "" : "cortex_m vector_catch none; ";
        if (profile.IsRp2350)
            openocd.AddRange(["-c", targetName + " configure -event gdb-detach {studiox_rp2350_detach}"]);
        else
            openocd.AddRange(["-c", targetName + " configure -event gdb-detach { " + detach + "resume; poll; if {[[target current] curstate] ne \"running\"} { error \"Target did not resume\" }; echo STUDIOX_DETACHED_RUNNING }"]);
        return new(tools.Tool("openocd"), openocd.ToArray(), tools.Tool("gdb"), ["--interpreter=mi2", "--nx", "--quiet"],
            ["-gdb-set auto-load off", "-gdb-set mi-async on", "-gdb-set pagination off", "-gdb-set confirm off", "-gdb-set may-call-functions off",
             // 不套用 F407 的比较器数量；OpenOCD 按实际目标资源插入，资源耗尽时保留原始诊断。
             "-gdb-set remote hardware-breakpoint-limit unlimited",
             .. (profile.IsWch ? new[] { "-gdb-set architecture riscv:rv32", "-gdb-set mem inaccessible-by-default off" } : Array.Empty<string>()),
             "-file-exec-and-symbols " + MiRecord.Quote(Path.GetFullPath(elf).Replace('\\', '/')),
             "-target-select extended-remote 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
             "-interpreter-exec console \"monitor halt\"", "-interpreter-exec console \"monitor studiox_check_target\"",
             .. (profile.IsRp2350 ? new[] { "-interpreter-exec console \"monitor studiox_rp2350_readonly\"" } : Array.Empty<string>()),
             "-interpreter-exec console " + MiRecord.Quote("monitor verify_image " + TclQuote(Path.GetFullPath(elf).Replace('\\', '/')) + " 0 elf")]);
    }
    private static string TclQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("[", "\\[").Replace("]", "\\]") + "\"";
}

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class ProbePlanChecks
{
    public static async Task RunAsync(string project, DownloadConfiguration configuration, ResolvedToolset tools,
        OpenOcdService downloads, string output, Action<string> pass)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var elf = Path.Combine(project, ".build/firmware.elf");
        var dap = configuration with { Options = new("cmsis-dap", 1000, "serial $[literal]") };
        foreach (var id in new[] { "stlink", "cmsis-dap" })
        {
            var selected = dap with { Options = dap.Options with { ProbeId = id } };
            var plan = OpenOcdDebugPlanner.Create(project, selected, tools, elf, 42345);
            Check(plan.OpenOcdArguments.Contains(Path.Combine(tools.ResourceDirectory("openocdScripts"), "interface", id + ".cfg")), "Selected probe interface");
            Check(plan.OpenOcdArguments.All(c => !c.EndsWith(id == "stlink" ? "cmsis-dap.cfg" : "stlink.cfg", StringComparison.Ordinal)), "No fallback to a different probe");
            Check(plan.GdbArguments.Contains("--interpreter=mi2") && plan.InitializeCommands.Any(c => c.Contains("127.0.0.1:42345", StringComparison.Ordinal)), "GDB port");
            Check(plan.OpenOcdArguments.Contains("bindto 127.0.0.1") && plan.OpenOcdArguments.Contains("transport select swd"), "Loopback and SWD");
            Check(plan.OpenOcdArguments.Contains("adapter speed 1000") && plan.OpenOcdArguments.Contains("adapter serial \"serial \\$\\[literal\\]\""), "Speed and serial escaping");
            Check(plan.InitializeCommands.All(c => !c.Contains("target-download", StringComparison.Ordinal)) && plan.OpenOcdArguments.All(c => !c.Contains("flash write_image", StringComparison.Ordinal)), "Attach cannot program flash");
            Check(plan.OpenOcdArguments.Contains("gdb flash_program disable") && plan.OpenOcdArguments.Contains("$_TARGETNAME configure -work-area-size 0 -work-area-backup 1"), "Flash and live RAM protection");
            Check(plan.OpenOcdArguments.Any(c => c.Contains("STUDIOX_DETACHED_RUNNING", StringComparison.Ordinal)), "Release must confirm target resumes");
            if (id == "cmsis-dap")
                Check(plan.OpenOcdArguments.All(c => !c.Contains("backend hid", StringComparison.Ordinal) && !c.Contains("backend usb_bulk", StringComparison.Ordinal)), "CMSIS-DAP v1/v2 auto backend");
            await downloads.SaveOptionsAsync(project, selected.Options);
            Check((await downloads.ConfigurationAsync(project))!.Options == selected.Options, "Probe settings persist");
            await JsonStore.WriteAsync(Path.Combine(output, id + "-session-plan.json"), plan);
            pass(id + ": selected interface, speed/serial persistence, MI/SWD, loopback port, attach protection and resume-on-detach; no hardware executed");
        }
        void Reject(DownloadConfiguration invalid, string code)
        {
            try { OpenOcdDebugPlanner.Create(project, invalid, tools, elf); }
            catch (StudioXException ex) when (ex.Code == code) { return; }
            throw new InvalidOperationException("Invalid debug configuration accepted: " + code);
        }
        Reject(dap with { Options = dap.Options with { ProbeId = "jlink" } }, "DEBUG_PROBE");
        Reject(dap with { Device = dap.Device with { Id = "STM32F103C8" } }, "DEBUG_TARGET"); // F407 内存布局不能冒充 F103。
        Reject(dap with { Device = dap.Device with { Id = "STM32F407FAKE" } }, "DEBUG_TARGET");
        Reject(dap with { OpenOcd = dap.OpenOcd with { Probes = dap.OpenOcd.Probes.Where(p => p.Id != "cmsis-dap").ToArray() } }, "DEBUG_PROBE");
        Reject(dap with { OpenOcd = dap.OpenOcd with { Probes = dap.OpenOcd.Probes.Select(p => p.Id == "cmsis-dap" ? p with { Transport = "jtag" } : p).ToArray() } }, "DEBUG_CONFIG");
        Reject(dap with { OpenOcd = dap.OpenOcd with { Probes = dap.OpenOcd.Probes.Select(p => p.Id == "cmsis-dap" ? p with { InterfaceScript = "interface/stlink.cfg" } : p).ToArray() } }, "DEBUG_CONFIG");
        Reject(dap with { Options = dap.Options with { SpeedKhz = 0 } }, "DEBUG_OPTIONS");
        Reject(dap with { Options = dap.Options with { Serial = "serial\ncommand" } }, "DEBUG_OPTIONS");
        await downloads.SaveOptionsAsync(project, configuration.Options);
        pass("Unsupported probe/target, missing or mismatched DAP configuration, invalid speed/serial rejected before starting processes");
    }
}

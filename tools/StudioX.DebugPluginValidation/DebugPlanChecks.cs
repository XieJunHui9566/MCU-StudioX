namespace StudioX.DebugPluginValidation;

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class DebugPlanChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var scripts = Path.Combine(root, "plan-tools/scripts/interface");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "stlink.cfg"), "# offline fixture");
        File.WriteAllText(Path.Combine(scripts, "cmsis-dap.cfg"), "# offline fixture");
        var manifest = new ProjectManifest(1, "offline-plan", "", "", "", "STM32F407ZGT6", "", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1", ProjectKind.CubeMx,
            new CubeMxProjectSettings("board.ioc", "cmake/gcc.cmake", "Debug", "Debug"));
        var project = Path.Combine(root, "plan-project");
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest);
        var configuration = (await new OpenOcdService(new ToolsetCatalog(Path.Combine(root, "plan-tools"))).ConfigurationAsync(project))!;
        var tools = new ResolvedToolset(new ToolsetManifest(1, "arm.gnu", "1.0.0", "win-x64", "arm-gnu-15.2.rel1",
            new()
            {
                ["openocd"] = "openocd.exe",
                ["gdb"] = "gdb.exe"
            }, new(), ResourceDirectories: new()
            {
                ["openocdScripts"] = "scripts"
            }), Path.Combine(root, "plan-tools"), "offline");
        var normal = OpenOcdDebugPlanner.Create(root, configuration, tools, Path.Combine(root, "fixture.elf"));
        var reset = OpenOcdDebugPlanner.Create(root, configuration, tools, Path.Combine(root, "fixture.elf"), connectUnderReset: true);
        check(!normal.OpenOcdArguments.Any(a => a.Contains("connect_assert_srst") || a.Contains("reset halt")), "normal attach never silently chooses reset connection");
        check(Array.IndexOf(reset.OpenOcdArguments, "gdb flash_program disable") < Array.IndexOf(reset.OpenOcdArguments, "init; reset halt; echo STUDIOX_RESET_HALTED") &&
            reset.OpenOcdArguments.Contains("reset_config srst_only srst_nogate connect_assert_srst"), "explicit reset plan disables flash programming before initialization");
        check(normal.InitializeCommands.Contains("-gdb-set remotetimeout 60") && reset.InitializeCommands.Any(a => a.Contains("monitor verify_image")), "slow link tolerance keeps board image verification mandatory");
        try
        {
            _ = OpenOcdDebugPlanner.Create(root, configuration with
            {
                Options = configuration.Options with
                {
                    ProbeId = "cmsis-dap"
                }
            }, tools, Path.Combine(root, "fixture.elf"), connectUnderReset: true);
            check(false, "unsupported reset probe rejected");
        }
        catch (StudioXException error) { check(error.Code == "DEBUG_RESET_CONNECT", "unsupported reset probe rejected before any tool launch"); }
    }
}

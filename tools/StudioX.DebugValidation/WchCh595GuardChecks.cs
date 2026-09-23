using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class WchCh595GuardChecks
{
    public static async Task RunAsync(string project, DeviceDefinition device, DownloadPreparation plan)
    {
        if (device.FlashBytes != 256 * 1024 || device.RamBytes != 32 * 1024 ||
            device.OpenOcd?.ApplicationFlashBytes != 240 * 1024)
            throw new InvalidOperationException("CH595 physical and application memory ranges must match the vendor SDK");
        var operation = plan.Arguments[^1];
        var interfaceFile = Path.Combine(project, "device/interface/wch-link.cfg");
        if (operation.IndexOf("studiox_check_target", StringComparison.Ordinal) < 0 ||
            operation.IndexOf("studiox_check_target", StringComparison.Ordinal) >= operation.IndexOf("flash write_image", StringComparison.Ordinal) ||
            !operation.Contains("reset halt; resume", StringComparison.Ordinal) ||
            !File.ReadAllText(interfaceFile).Contains("page_erase", StringComparison.Ordinal))
            throw new InvalidOperationException("CH595 requires guarded page programming and explicit resume");

        // noinit + 模拟只读寄存器：执行器件包的真实 Tcl 保护过程，不连接 USB 或目标板。
        var script = "set fake_chip 0x97; set fake_status 0; set fake_read_error 0; " +
            "proc read_memory {address width count} {global fake_chip fake_status fake_read_error; " +
            "if {$fake_read_error} {error SIMULATED_READ_FAILURE}; " +
            "if {$width != 8 || $count != 1} {error UNEXPECTED_READ_WIDTH}; " +
            "if {$address == 0x40001041} {return [list $fake_chip]}; " +
            "if {$address == 0x40001809} {return [list $fake_status]}; error UNEXPECTED_READ_ADDRESS}; " +
            "proc expect_reject {} {if {![catch {studiox_check_target}]} {error GUARD_NOT_REJECTED}}; " +
            "studiox_check_target; set fake_chip 0x92; expect_reject; " +
            "set fake_chip 0x97; set fake_status 1; expect_reject; " +
            "set fake_status 0; set fake_read_error 1; expect_reject; " +
            "echo STUDIOX_CH595_GUARDS_OK; shutdown";
        var dry = await new ProcessRunner().RunAsync(new(plan.Tools.Tool("openocd"), ["-c", "noinit", .. plan.Arguments[..^2], "-c", script],
            project, TimeSpan.FromSeconds(15), ToolsetEnvironment.Create(plan.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(project, "ch595-guards.log"), dry.StandardOutput + dry.StandardError);
        if (!dry.Success || !(dry.StandardOutput + dry.StandardError).Contains("STUDIOX_CH595_GUARDS_OK", StringComparison.Ordinal))
            throw new InvalidOperationException(dry.StandardOutput + dry.StandardError);

        var config = plan.Configuration;
        foreach (var invalid in new[]
        {
            config with { OpenOcd = config.OpenOcd with { ApplicationFlashBytes = 256 * 1024 } },
            config with { OpenOcd = config.OpenOcd with { TargetScript = "debug/ch32v203c8t6.cfg" } },
            config with { OpenOcd = config.OpenOcd with { Probes = [config.OpenOcd.Probes[0] with { Transport = "swd" }] } }
        })
        {
            try
            {
                OpenOcdService.CreateArguments(project, invalid, invalid.Options, plan.Tools, plan.Image);
            }
            catch (StudioXException ex) when (ex.Code == "DOWNLOAD_TARGET") { continue; }
            throw new InvalidOperationException("CH595 download accepted a mismatched target, range or probe");
        }
    }
}

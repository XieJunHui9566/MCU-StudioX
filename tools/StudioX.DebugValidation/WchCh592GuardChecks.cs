using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class WchCh592GuardChecks
{
    public static async Task RunAsync(string project, DeviceDefinition device, DownloadPreparation plan)
    {
        if (device.FlashBytes != 448 * 1024 || device.RamBytes != 26 * 1024 ||
            device.OpenOcd?.ApplicationFlashBytes != 448 * 1024)
            throw new InvalidOperationException("CH592 Code Flash and SRAM ranges must match the vendor SDK");
        var operation = plan.Arguments[^1];
        var interfaceFile = Path.Combine(project, "device/interface/wch-link.cfg");
        if (operation.IndexOf("studiox_check_target", StringComparison.Ordinal) < 0 ||
            operation.IndexOf("studiox_check_target", StringComparison.Ordinal) >= operation.IndexOf("flash write_image", StringComparison.Ordinal) ||
            !operation.Contains("reset halt; resume", StringComparison.Ordinal) ||
            !File.ReadAllText(interfaceFile).Contains("page_erase", StringComparison.Ordinal))
            throw new InvalidOperationException("CH592 requires guarded page programming and explicit resume");

        // 执行包内真实 Tcl；模拟寄存器读取且 OpenOCD noinit，绝不访问 USB 或目标板。
        var script = "set fake_chip 0x92; set fake_cfg 0x11; set fake_read_error 0; " +
            "proc read_memory {address width count} {global fake_chip fake_cfg fake_read_error; " +
            "if {$fake_read_error} {error SIMULATED_READ_FAILURE}; " +
            "if {$width != 8 || $count != 1} {error UNEXPECTED_READ_WIDTH}; " +
            "if {$address == 0x40001041} {return [list $fake_chip]}; " +
            "if {$address == 0x40001045} {return [list $fake_cfg]}; error UNEXPECTED_READ_ADDRESS}; " +
            "proc expect_reject {} {if {![catch {studiox_check_target}]} {error GUARD_NOT_REJECTED}}; " +
            "studiox_check_target; set fake_chip 0x97; expect_reject; " +
            "set fake_chip 0x92; set fake_cfg 0x10; expect_reject; " +
            "set fake_cfg 0x01; expect_reject; " +
            "set fake_cfg 0x11; set fake_read_error 1; expect_reject; " +
            "echo STUDIOX_CH592_GUARDS_OK; shutdown";
        var dry = await new ProcessRunner().RunAsync(new(plan.Tools.Tool("openocd"), ["-c", "noinit", .. plan.Arguments[..^2], "-c", script],
            project, TimeSpan.FromSeconds(15), ToolsetEnvironment.Create(plan.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(project, "ch592-guards.log"), dry.StandardOutput + dry.StandardError);
        if (!dry.Success || !(dry.StandardOutput + dry.StandardError).Contains("STUDIOX_CH592_GUARDS_OK", StringComparison.Ordinal))
            throw new InvalidOperationException(dry.StandardOutput + dry.StandardError);

        var config = plan.Configuration;
        foreach (var invalid in new[]
        {
            config with { OpenOcd = config.OpenOcd with { ApplicationFlashBytes = 512 * 1024 } },
            config with { OpenOcd = config.OpenOcd with { TargetScript = "debug/ch595f.cfg" } },
            config with { OpenOcd = config.OpenOcd with { Probes = [config.OpenOcd.Probes[0] with { Transport = "swd" }] } }
        })
        {
            try
            {
                OpenOcdService.CreateArguments(project, invalid, invalid.Options, plan.Tools, plan.Image);
            }
            catch (StudioXException ex) when (ex.Code == "DOWNLOAD_TARGET") { continue; }
            throw new InvalidOperationException("CH592 download accepted a mismatched target, range or probe");
        }
    }
}

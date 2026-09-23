using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class WchV203GuardChecks
{
    public static async Task RunAsync(string packRoot, string project, DeviceDefinition device, DownloadPreparation plan)
    {
        using var definitions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packRoot, "vendor/devices.json")));
        var definition = definitions.RootElement.EnumerateArray().Single(d => d.GetProperty("id").GetString() == device.Id);
        var chips = definition.GetProperty("chipIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
        var operation = plan.Arguments[^1];
        if (operation.IndexOf("studiox_check_target", StringComparison.Ordinal) >= operation.IndexOf("flash write_image", StringComparison.Ordinal) ||
            !operation.Contains("reset halt; resume", StringComparison.Ordinal) ||
            !File.ReadAllText(Path.Combine(project, "device/interface/wch-link.cfg")).Contains("\npage_erase", StringComparison.Ordinal))
            throw new InvalidOperationException("WCH guarded page programming and run sequence required");
        // noinit：用模拟的只读寄存器执行实际 Tcl；不会初始化 USB 或读写任何芯片。
        var script = "set fake_chip 0; set fake_user 0x00ff; set fake_rdp 0x5aa5; " +
            "proc read_memory {address width count} {global fake_chip fake_user fake_rdp; if {$address == 0x1ffff704} {return [list $fake_chip]}; if {$address == 0x1ffff802} {return [list $fake_user]}; if {$address == 0x1ffff800} {return [list $fake_rdp]}; error UNEXPECTED_READ}; " +
            "proc expect_reject {reason} {if {![catch {studiox_check_target} message] || [string first $reason $message] < 0} {error GUARD_NOT_REJECTED}}; " +
            // 覆盖多芯片 ID 的 F6P6 以及所有修订号；型号其他位必须完整匹配。
            string.Join(" ", chips.Select(chip => $"for {{set revision 0}} {{$revision < 16}} {{incr revision}} {{set fake_chip [expr {{{chip} | ($revision << 4)}}]; studiox_check_target}};")) +
            "set fake_chip 0x30700568; expect_reject {Target mismatch}; " +
            $"set fake_chip {chips[0]}; set fake_rdp 0; expect_reject {{Read protection}}; set fake_rdp 0x5aa5; ";
        if (device.Id == "CH32V203RBT6")
            script += "set fake_user 0xc03f; expect_reject {Memory split}; set fake_user 0x807f; expect_reject {Memory split}; " +
                "set fake_user 0x417f; expect_reject {Invalid option}; set fake_user 0x40bf; studiox_check_target; set fake_user 0x00ff; studiox_check_target; ";
        else
            script += "set fake_user 0xc03f; studiox_check_target; set fake_user 0x00ff; studiox_check_target; ";
        script += "echo STUDIOX_V203_GUARDS_OK; shutdown";
        var result = await new ProcessRunner().RunAsync(new(plan.Tools.Tool("openocd"), plan.Arguments[..^2].Concat(["-c", script]).ToArray(), project,
            TimeSpan.FromSeconds(15), ToolsetEnvironment.Create(plan.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(project, "download-guards.log"), result.StandardOutput + result.StandardError);
        if (!result.Success || !(result.StandardOutput + result.StandardError).Contains("STUDIOX_V203_GUARDS_OK", StringComparison.Ordinal))
            throw new InvalidOperationException(result.StandardOutput + result.StandardError);
    }
}

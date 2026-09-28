using System.Text;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class GpioElectricalChecks
{
    internal static async Task RunAsync(string toolsets, string directory, string archive)
    {
        var output = Path.GetFullPath(directory);
        if (Directory.Exists(output)) { throw new IOException("Use a new output directory."); }
        Directory.CreateDirectory(output);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(Path.GetFullPath(archive));
        var root = Path.Combine(output, "电气配置 fixture");
        await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", "minimal", "gpio_electrical", root);
        var tools = new ToolsetCatalog(Path.GetFullPath(toolsets));
        var planner = new Ag32PinPlanService(tools);
        var builder = new Ag32PinMappingBuildService(tools);
        var source = Path.Combine(root, "logic/pins.ve");
        await File.WriteAllBytesAsync(source, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("HSECLK 8\r\nSYSCLK 200\r\nBUSCLK 100\r\nGPIO4_4 PIN_2:OUTPUT #LED1 # 原有中文说明\r\n")]);
        var checks = new List<string>();
        void Check(bool okay, string message)
        {
            if (!okay) { throw new InvalidOperationException(message); }
            checks.Add(message); Console.WriteLine("PASS " + message);
        }
        var hashes = new HashSet<string>();
        foreach (var pin in new[]
        {
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1", "UP", "OPEN_DRAIN"),
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1", "DOWN", "PUSH_PULL"),
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1", "NONE", "OPEN_DRAIN"),
            new Ag32PinAssignment("GPIO4_4", 2, "INPUT", "LED1", "UP"),
            new Ag32PinAssignment("GPIO4_4", 2, "INPUT", "LED1", "DOWN"),
            new Ag32PinAssignment("GPIO4_4", 2, "INPUT", "LED1"),
            new Ag32PinAssignment("GPIO4_4", 2, null, "LED1", "UP", "OPEN_DRAIN"),
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1")
        })
        {
            var name = $"{pin.Direction ?? "INOUT"}-{pin.Pull}-{pin.OutputType}";
            var before = await planner.ReadAsync(root);
            var plan = await planner.ApplyAsync(root, before, [pin], before.Clocks);
            var reload = await planner.ReadAsync(root);
            Check(reload.CanEdit && reload.Assignments.Single() == pin, name + " saves and reloads complete GPIO settings");
            var bytes = await File.ReadAllBytesAsync(source);
            var text = Encoding.UTF8.GetString(bytes);
            Check(bytes is [0xef, 0xbb, 0xbf, ..] && text.Contains("#LED1 # 原有中文说明") && text.Contains("\r\n"), name + " preserves name, comment, BOM and CRLF");
            Check(File.Exists(Path.Combine(plan.ConstraintDirectory, "studiox-gpio.asf")), name + " creates inspectable electrical constraints before compilation");
            var build = await builder.BuildAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), build.Log);
            Check(build.Success && !build.Log.Contains("Warn:", StringComparison.Ordinal), name + " real Supra builds without warnings and verifies routed IO parameters");
            var image = await builder.ValidateBuiltAsync(root);
            hashes.Add(image.Sha256);
            foreach (var file in new[] { "studiox-gpio.asf", "pins_routed.v", "logic_db/io.asf" })
                File.Copy(Path.Combine(root, ".build/ag32-mapping", file), Path.Combine(output, name + "-" + Path.GetFileName(file)));
        }
        Check(hashes.Count >= 6, "electrical choices change actual bitstreams, not just UI metadata");
        Check(!File.ReadAllText(source).Contains("#@StudioX:GPIO"), "returning to default removes electrical metadata without removing user comments");
        var original = await File.ReadAllBytesAsync(source);
        foreach (var invalid in new[]
        {
            new Ag32PinAssignment("GPIO4_4", 2, "INPUT", "LED1", "UP", "OPEN_DRAIN"),
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1", "bad-value"),
            new Ag32PinAssignment("GPIO4_4", 2, "OUTPUT", "LED1", "NONE", "bad-value"),
            new Ag32PinAssignment("UART0_UARTTXD", 2, "OUTPUT", "LED1", "UP")
        })
        {
            var before = await planner.ReadAsync(root);
            try { await planner.ApplyAsync(root, before, [invalid], before.Clocks); throw new InvalidOperationException("Invalid GPIO options accepted."); }
            catch (StudioXException error) when (error.Code == "AG32_PIN_PLAN_CONFLICT")
            { Check(original.SequenceEqual(File.ReadAllBytes(source)), "invalid GPIO options rejected transactionally: " + invalid); }
        }
        var asf = Path.Combine(root, ".build/ag32-mapping/studiox-gpio.asf");
        var asfText = await File.ReadAllTextAsync(asf);
        await File.AppendAllTextAsync(asf, "\n# changed\n");
        try { await builder.ValidateBuiltAsync(root); throw new InvalidOperationException("Changed ASF accepted"); }
        catch (StudioXException error) when (error.Code == "AG32_GPIO_OPTIONS") { Check(true, "changed electrical constraint rejects old image receipt"); }
        await File.WriteAllTextAsync(asf, asfText);
        var routed = Path.Combine(root, ".build/ag32-mapping/pins_routed.v");
        var routedText = await File.ReadAllTextAsync(routed);
        await File.WriteAllTextAsync(routed, routedText.Replace("PIN_2_iobuf.CFG_KEEP = 2'b00", "PIN_2_iobuf.CFG_KEEP = 2'b10"));
        try { await builder.ValidateBuiltAsync(root); throw new InvalidOperationException("Changed physical IO accepted"); }
        catch (StudioXException error) when (error.Code == "AG32_GPIO_OPTIONS") { Check(true, "wrong actual IO pull setting rejects old image receipt"); }
        await File.WriteAllTextAsync(routed, routedText);
        await File.WriteAllTextAsync(source, File.ReadAllText(source).Replace(" #LED1", " #@StudioX:GPIO pull=BAD output=PUSH_PULL #LED1"));
        try { await planner.ReadAsync(root); throw new InvalidOperationException("Malformed GPIO metadata accepted"); }
        catch (StudioXException error) when (error.Code == "AG32_GPIO_OPTIONS") { Check(true, "malformed metadata cannot silently become floating push-pull"); }
        await File.WriteAllBytesAsync(source, original);
        var fresh = await planner.ReadAsync(root);
        await planner.ApplyAsync(root, fresh, [new("GPIO4_4", 2, "OUTPUT", "LED1", "UP", "OPEN_DRAIN")], fresh.Clocks);
        var fullBuild = await new BuildService(tools).BuildAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "joint-build.log"), fullBuild.Log);
        Check(fullBuild.Success && File.Exists(Path.Combine(root, ".build/firmware.elf")), "actual MCU compiler links generated system code and matching open-drain mapping");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, checks, hardwareAccessed = false });
        Console.WriteLine($"PASS {checks.Count} GPIO electrical checks; converter, GCC and Supra only; no hardware access.");
    }
}

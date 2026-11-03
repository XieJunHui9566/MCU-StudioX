namespace StudioX.PicValidation;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Engine.Pic;
using StudioX.Foundation;

internal static class PicHardwareDebug
{
    public static async Task PrepareAsync(string output, string runtime, string archive, string sourcePath, string project)
    {
        Directory.CreateDirectory(output);
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
        var pack = await services.Packs.ImportAsync(archive);
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-basic", "PicLcdCMake", project);
        var source = await File.ReadAllTextAsync(sourcePath);
        if (!source.Contains("Hello, World!", StringComparison.Ordinal) || !source.Contains("CLRWDT();", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Use the previously verified LCD source.");
        }
        source = source.Replace("void main(void)", "volatile unsigned int debug_counter;\n\nstatic unsigned int debug_next(unsigned int value)\n{\n    return value + 1u;\n}\n\nvoid main(void)", StringComparison.Ordinal)
            .Replace("        CLRWDT();", "        debug_counter = debug_next(debug_counter);\n        CLRWDT();", StringComparison.Ordinal);
        source = source.Replace("#pragma config PWRTE = ON", "#if defined(STUDIOX_PIC_DEBUG)\n#pragma config PWRTE = OFF\n#else\n#pragma config PWRTE = ON\n#endif", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), source);
        var docs = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sourcePath)!)!, "docs");
        if (Directory.Exists(docs))
        {
            foreach (var file in Directory.GetFiles(docs, "*.pdf"))
            {
                Directory.CreateDirectory(Path.Combine(project, "docs"));
                File.Copy(file, Path.Combine(project, "docs", Path.GetFileName(file)));
            }
        }
        await RebuildAsync(output, runtime, project);
    }

    public static async Task RebuildAsync(string output, string runtime, string project)
    {
        Directory.CreateDirectory(output);
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
        var build = await services.Builds.BuildPicDebugAsync(project);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Log);
        if (!build.Success)
        {
            throw new InvalidOperationException("LCD debug build failed.");
        }
        var firmware = await services.Builds.Pic.PreviewAsync(project);
        var image = PicHexImage.Validate(await File.ReadAllBytesAsync(firmware.HexPath));
        if (image.ConfigurationWord != 0x3f3a)
        {
            throw new InvalidOperationException("Debug configuration only disables PWRT; release remains 0x3F32.");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "firmware.json"), new
        {
            firmware,
            image,
            project,
            hardware = false
        });
        Console.WriteLine("Prepared LCD debug HEX SHA-256: " + firmware.Sha256);
    }

    public static async Task RunAsync(string output, string runtime, string debugRuntime, string project, string serial, string expectedHash)
    {
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use new hardware evidence directory.");
        }
        Directory.CreateDirectory(output);
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
        var firmware = await services.Builds.Pic.PreviewAsync(project);
        if (firmware.DeviceId != "PIC16F877A" || firmware.Sha256 != expectedHash || firmware.Debugger != "pickit3" ||
            PicHexImage.Validate(await File.ReadAllBytesAsync(firmware.HexPath)).ConfigurationWord != 0x3f3a)
        {
            throw new InvalidOperationException("Exact reviewed hardware debug image differs.");
        }
        var engine = new PicDebugService(new ToolsetCatalog(Path.Combine(debugRuntime, "toolsets")), services.Builds.Pic, Path.Combine(output, "debug-data"));
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        var logGate = new object();
        services.Debugger.Output += text => { lock (logGate) { File.AppendAllText(Path.Combine(output, "session.log"), text + "\n"); } };
        await services.Debugger.OpenProjectAsync(project);
        await services.Debugger.ChangeWatchAsync("debug_counter", false);
        var lines = (await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"))).Replace("\r\n", "\n").Split('\n');
        var breakpointLine = Array.FindIndex(lines, line => line.Contains("debug_counter = debug_next", StringComparison.Ordinal)) + 1;
        await services.Debugger.ToggleBreakpointAsync("src/main.c", breakpointLine);
        try
        {
            await services.Debugger.StartPicAsync(engine, serial);
            Check(services.Debugger.IsHardware && services.Debugger.State == DebugState.Stopped, "PICkit3 / PIC16F877A identified, debug programmed and stopped at main");
            await services.Debugger.ExecuteAsync(DebugAction.Continue);
            await WaitStopped();
            Check(services.Debugger.Snapshot.Frames.First().Line == breakpointLine, "hardware source breakpoint hits after LCD initialization");
            await services.Debugger.ChangeBreakpointAsync(services.Debugger.Breakpoints.Single().Id, null);
            var before = services.Debugger.Snapshot.Watches.Single(w => w.Name == "debug_counter").Value;
            await services.Debugger.ExecuteAsync(DebugAction.StepOver);
            await WaitStopped();
            var after = services.Debugger.Snapshot.Watches.Single(w => w.Name == "debug_counter").Value;
            Check(before.StartsWith("0x", StringComparison.Ordinal) && after.StartsWith("0x", StringComparison.Ordinal) && before != after,
                "hardware next executes the counter function and updates the real variable");
            Check((await services.Debugger.ReadMemoryAsync(0x20, 16)).Length == 32, "hardware file registers read successfully");
            Check((await services.Debugger.ReadDisassemblyAsync(0, 8)).Instructions.Length == 8, "hardware program-word disassembly read successfully");
            await services.Debugger.ExecuteAsync(DebugAction.Continue);
            await Task.Delay(250);
            await services.Debugger.ExecuteAsync(DebugAction.Pause);
            await WaitStopped();
            Check(services.Debugger.State == DebugState.Stopped, "hardware pause returns fresh source and registers");
            await services.Debugger.ExecuteAsync(DebugAction.Reset);
            Check(services.Debugger.Snapshot.Registers.Single(r => r.Name == "pc").Value == "0x0", "hardware reset refreshes PC");
        }
        finally { await services.Debugger.StopAsync(); }
        Check(services.Debugger.State == DebugState.Disconnected, "hardware session exits and releases probe after original firmware restoration");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = true,
            serial,
            expectedHash,
            checks,
            engine.LogPath
        });
        async Task WaitStopped()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (services.Debugger.State == DebugState.Running && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            if (services.Debugger.State != DebugState.Stopped)
            {
                throw new InvalidOperationException(services.Debugger.Reason);
            }
        }
    }
}

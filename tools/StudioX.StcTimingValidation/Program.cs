using StudioX.Application.StcTiming;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length != 3)
{
    Console.Error.WriteLine("StudioX.StcTimingValidation <new-output> <STC8.mcupack> <tool-runtime>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root))
{
    throw new InvalidOperationException("Evidence directory must not exist.");
}
Directory.CreateDirectory(root);
var checks = new List<string>();
var builds = new List<object>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> operation, string name)
{
    try
    {
        await operation();
    }
    catch (StudioXException)
    {
        Check(true, name);
        return;
    }
    throw new InvalidOperationException("Did not reject: " + name);
}
Task Invoke(Action operation)
{
    operation();
    return Task.CompletedTask;
}
try
{
    var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(Path.GetFullPath(args[1]));
    var service = new StcTimingService();
    var builder = new BuildService(new ToolsetCatalog(Path.Combine(Path.GetFullPath(args[2]), "toolsets")));
    var path = Path.Combine(root, "boundary");
    var selected = pack.Manifest.Devices.Single(d => d.Id == "IAP15F2K61S2");
    await new ProjectService().CreateAsync(pack, selected.Id, selected.Templates[0].Id, "timing_check", path);
    var context = await service.ReadAsync(path);
    var settings = context.Settings;
    await Reject(() => Invoke(() => service.GenerateDelay(context, settings, "sx_delay")), "missing project clock never assumes 12 MHz");
    settings = settings with
    {
        ReferenceClockHz = 12000000,
        TimerDivider = 1
    };
    var delay = service.GenerateDelay(context, settings, "sx_delay");
    Check(delay.TimerTicks == 12000 && delay.Reload == 0xD120 && delay.ActualValue == 1000 && delay.ErrorPercent == 0, "12 MHz / 1T / 1 ms matches independent expected reload D120");
    var uart = service.GenerateUart(context, settings with
    {
        BaudRate = 115200
    }, "sx_uart_init");
    Check(uart.TimerTicks == 26 && uart.Reload == 0xFFE6 && decimal.Abs(uart.ActualValue - 115384.615384m) < 0.000001m && uart.CanInsert, "12 MHz / Timer2 / 115200 uses 26 ticks and FFE6");
    var uartExact = service.GenerateUart(context, settings with
    {
        ReferenceClockHz = 11059200,
        BaudRate = 115200
    }, "sx_uart_init");
    Check(uartExact.ErrorPercent == 0 && uartExact.Reload == 0xFFE8, "11.0592 MHz Timer2 gives exact 115200 baud");
    var classic = service.GenerateUart(context, settings with
    {
        ReferenceClockHz = 11059200,
        TimerDivider = 12,
        BaudGenerator = "timer1"
    }, "sx_uart_init");
    Check(classic.Reload == 0xFD && classic.ActualValue == 9600 && classic.Code.Contains("PCON = (PCON & 0x7F) | 0x00;", StringComparison.Ordinal), "Timer1 mode2 / 12T gives FD and SMOD0 at exact 9600");
    var doubled = service.GenerateUart(context, settings with
    {
        ReferenceClockHz = 11059200,
        TimerDivider = 1,
        BaudGenerator = "timer1",
        BaudRate = 230400
    }, "sx_uart_init");
    Check(doubled.TimerTicks == 3 && doubled.ActualValue == 230400 && doubled.Code.Contains("| 0x80;", StringComparison.Ordinal), "Timer1 compares SMOD0/1 and chooses exact doubled baud");
    var approximate = service.GenerateUart(context, settings with
    {
        BaudGenerator = "timer1",
        BaudRate = 115200
    }, "sx_uart_init");
    Check(!approximate.CanInsert && decimal.Abs(approximate.ErrorPercent) > 2, "poor Timer1 baud remains visible and blocks insertion");
    await Reject(() => Invoke(() => service.PrepareInsertion(approximate, "")), "unsafe UART result cannot be inserted through application service");
    var mon = context with
    {
        Mon51 = true
    };
    Check(!service.GenerateUart(mon, settings, "sx_uart_init").CanInsert, "Mon51 default UART pins are blocked");
    var alternate = service.GenerateUart(mon, settings with
    {
        UartPins = "p36"
    }, "sx_uart_init");
    Check(alternate.CanInsert && alternate.Code.Contains("P_SW1 = (P_SW1 & 0x3F) | 0x40;", StringComparison.Ordinal) && alternate.Code.Contains("P3M1 = (P3M1 & 0x7F) | 0x40;", StringComparison.Ordinal), "Mon51 alternate pins configure selected RX input and TX output");
    var longDelay = service.GenerateDelay(context, settings with
    {
        ReferenceClockHz = 40000000,
        DelayMicroseconds = 10000000
    }, "sx_delay_long");
    Check(longDelay.TimerTicks == 400000000 && longDelay.Code.Contains("blocks = 6103U", StringComparison.Ordinal) && longDelay.Reload == 0x7C00, "long delay splits full 16-bit periods without overflowing target unsigned int");
    var partial = service.GenerateDelay(context, settings with
    {
        ReferenceClockHz = 11059200
    }, "sx_delay_fraction");
    Check(partial.TimerTicks == 11059 && partial.ActualValue != 1000, "fractional tick displays quantization rather than claiming exact duration");
    Check(delay.Code.Contains("EA = saved_ea;", StringComparison.Ordinal) && delay.Code.Contains("if (TR0 || ET0", StringComparison.Ordinal) && delay.Code.Contains("TMOD & 0xF0", StringComparison.Ordinal), "delay checks resource use and preserves interrupt state and other timer mode");
    Check(delay.Code.Contains("INT_CLKO & 0x01", StringComparison.Ordinal) && uart.Code.Contains("INT_CLKO & 0x04", StringComparison.Ordinal), "enabled timer clock output is rejected instead of toggling unrelated or monitor pins");
    Check(service.PrepareInsertion(delay, "// header\r\n").Replace("\r\n", "", StringComparison.Ordinal).Contains('\n') == false, "insertion preserves CRLF throughout generated text");
    await Reject(() => Invoke(() => service.PrepareInsertion(delay, "void sx_delay(void);")), "duplicate function insertion rejected");
    await Reject(() => Invoke(() => service.PrepareInsertion(delay, "// sx_delay_wait exists")), "helper collision also rejected");
    foreach (var name in new[] { "main", "while", "sx_x);evil();", "sx_变量", new string('a', 50) })
    {
        await Reject(() => Invoke(() => service.GenerateDelay(context, settings, name)), "invalid function name rejected: " + name);
    }
    foreach (var invalid in new[] { settings with { ReferenceClockHz = 0 }, settings with { TimerDivider = 6 }, settings with { DelayMicroseconds = 0 }, settings with { BaudRate = int.MaxValue }, settings with { UartPins = "evil" } })
    {
        await Reject(() => Invoke(() => service.GenerateDelay(context, invalid, "sx_delay")), "invalid clock/timing inputs rejected");
    }
    await Reject(() => Invoke(() => service.GenerateDelay(context, settings with { ReferenceClockHz = 1000000, TimerDivider = 12, DelayMicroseconds = 1 }, "sx_delay")), "sub-tick delays cannot silently become zero");
    var stored = await service.SaveAsync(context, settings);
    Check(stored.Settings == settings && stored.ProjectClockHz is null && !File.Exists(Path.Combine(path, StcIspSettings.RelativePath)), "generator settings persist without creating ISP settings");
    await Reject(() => service.ValidateAsync(context), "saved parameter changes invalidate old generation context");
    await Reject(() => service.SaveAsync(context, settings), "stale parameter writer cannot overwrite current settings");
    await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200));
    var preset = await service.ReadAsync(path);
    Check(service.GenerateUart(preset, settings with
    {
        BaudRate = 115200
    }, "sx_uart_init").ActualValue == 115200, "saved project clock takes precedence over manual reference");
    await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath), new StcIspSettings(Port: "COM99", ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200, TransferBaud: 19200));
    await service.ValidateAsync(preset);
    Check(true, "ISP port and transport baud do not change generated user UART timing");
    await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 12000000));
    await Reject(() => service.ValidateAsync(preset), "project frequency change blocks stale insertion");
    await JsonStore.WriteAsync(Path.Combine(path, ProjectBuildSettings.RelativePath), new ProjectBuildSettings(Mon51Profile: true, DebugInfo: CompilerDebugInfo.Standard));
    var monitor = await service.ReadAsync(path);
    Check(monitor.Mon51 && !service.GenerateUart(monitor, settings, "sx_uart_init").CanInsert, "actual Mon51 build preset is respected");
    var missingHeader = Path.Combine(path, "device", "sdk", "include", "stc15.h");
    var headerText = await File.ReadAllTextAsync(missingHeader);
    await File.WriteAllTextAsync(missingHeader, headerText.Replace(" P_SW1;", " WRONG;", StringComparison.Ordinal));
    await Reject(() => service.ReadAsync(path), "missing declared register blocks generation");
    await File.WriteAllTextAsync(missingHeader, headerText.Replace("__sfr __at(0xA2) P_SW1;", "__sfr __at(0xA3) P_SW1;", StringComparison.Ordinal));
    await Reject(() => service.ReadAsync(path), "wrong register address blocks generation");
    await File.WriteAllTextAsync(missingHeader, headerText);

    foreach (var device in pack.Manifest.Devices)
    {
        var project = Path.Combine(root, "projects", device.Id);
        await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, "timing_check", project);
        var current = await service.ReadAsync(project);
        var values = current.Settings with
        {
            ReferenceClockHz = 11059200,
            TimerDivider = current.Profile.Family == "stc89" ? 12 : 1
        };
        var generatedDelay = service.GenerateDelay(current, values, "sx_delay");
        var generatedUart = service.GenerateUart(current, values, "sx_uart_init");
        Check(generatedUart.CanInsert, device.Id + " calculated baud accepted");
        await File.WriteAllTextAsync(Path.Combine(project, "src", "main.c"), generatedDelay.Code + generatedUart.Code + "\nvolatile unsigned char sx_result;\nvoid main(void) { sx_result = sx_uart_init(); for (;;) { sx_result = sx_delay(); } }\n");
        var report = await builder.BuildAsync(project);
        Check(report.Success, device.Id + " generated delay and UART compile and link with actual bundled SDCC");
        builds.Add(new
        {
            device = device.Id,
            passed = report.Success,
            log = report.LogPath
        });
        if (new[] { "IAP15F2K61S2", "STC89C52RC", "STC12C5A60S2", "STC8G1K08", "STC8H1K08" }.Contains(device.Id))
        {
            values = values with
            {
                DelayTimer = 1,
                DelayMicroseconds = 10000000,
                BaudGenerator = "timer1",
                UartPins = current.Profile.HasPinSwitch ? "p16" : "default"
            };
            var second = service.GenerateUart(current, values, "sx_uart_init");
            await File.WriteAllTextAsync(Path.Combine(project, "src", "main.c"), service.GenerateDelay(current, values, "sx_delay").Code + second.Code + "\nvoid main(void) { for (;;) {} }\n");
            await builder.SaveSettingsAsync(project, new ProjectBuildSettings(Optimization: CompilerOptimization.O0));
            var low = await builder.BuildAsync(project);
            Check(low.Success, device.Id + " alternate timer/pins/long delay compile under low optimization");
            builds.Add(new
            {
                device = device.Id,
                passed = low.Success,
                log = low.LogPath,
                variant = "alternate"
            });
        }
    }
    var guardedProject = Path.Combine(root, "projects", "IAP15F2K61S2");
    await JsonStore.WriteAsync(Path.Combine(guardedProject, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 12000000));
    await builder.SaveSettingsAsync(guardedProject, new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard, Mon51Profile: true));
    var guardedContext = await service.ReadAsync(guardedProject);
    var guardedSettings = guardedContext.Settings with
    {
        TimerDivider = 1,
        UartPins = "p36",
        BaudRate = 115200
    };
    var guardedSource = service.GenerateDelay(guardedContext, guardedSettings, "sx_delay").Code + service.GenerateUart(guardedContext, guardedSettings, "sx_uart_init").Code + "\nvoid main(void) { for (;;) {} }\n";
    await File.WriteAllTextAsync(Path.Combine(guardedProject, "src", "main.c"), guardedSource);
    var guardedBuild = await builder.BuildAsync(guardedProject);
    Check(guardedBuild.Success, "Mon51 alternate-pin functions compile with actual CDB/memory/clock preset");
    builds.Add(new
    {
        device = "IAP15F2K61S2",
        passed = guardedBuild.Success,
        log = guardedBuild.LogPath,
        variant = "Mon51"
    });
    await JsonStore.WriteAsync(Path.Combine(guardedProject, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200));
    var changedClock = await builder.BuildAsync(guardedProject);
    Check(!changedClock.Success && changedClock.Log.Contains("Project_clock_changed_regenerate_timing_functions", StringComparison.Ordinal), "actual compiler rejects old generated code after project clock change");
    await File.WriteAllTextAsync(Path.Combine(root, "expected-clock-rejection.log"), changedClock.Log);
    await JsonStore.WriteAsync(Path.Combine(guardedProject, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 12000000));
    var conflictCode = service.GenerateUart(guardedContext with
    {
        Mon51 = false
    }, guardedSettings with
    {
        UartPins = "default"
    }, "sx_uart_init").Code;
    await File.WriteAllTextAsync(Path.Combine(guardedProject, "src", "main.c"), conflictCode + "\nvoid main(void) { for (;;) {} }\n");
    var conflictBuild = await builder.BuildAsync(guardedProject);
    Check(!conflictBuild.Success && conflictBuild.Log.Contains("UART1_default_pins_conflict_with_Mon51", StringComparison.Ordinal), "actual compiler rejects ordinary default UART code when Mon51 is later enabled");
    await File.WriteAllTextAsync(Path.Combine(root, "expected-monitor-rejection.log"), conflictBuild.Log);
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
    {
        passed = true,
        hardware = false,
        checks,
        builds
    });
    Console.WriteLine($"PASS {checks.Count} checks; {builds.Count} actual SDCC builds");
}
catch (Exception ex)
{
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
    {
        passed = false,
        checks,
        builds,
        diagnostic = ex.ToString()
    });
    throw;
}
return 0;

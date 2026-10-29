namespace StudioX.StcConfiguratorValidation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.StcConfigurator;

internal static class GenerationChecks
{
    internal static async Task RunAsync(InstalledPack pack, string archive, string runtime, string hostRuntime, string output, Action<bool, string> check)
    {
        var outputRule = typeof(McuCodeGenerationService).GetMethod("SafeOutput", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var path in new[] { "src/generated/mcuconfig/Delay.c", "include/generated/mcuconfig/Delay.h", "src/generated/mcuconfig/uart.c" })
        {
            check((bool)outputRule.Invoke(null, [path])!, "generated ASCII filename accepted: " + path);
        }
        foreach (var path in new[] { "src/main.c", "src/generated/mcuconfig/../main.c", "src/generated/mcuconfig/Delay.c\n", "src/generated/mcuconfig/Delay.c.exe", "src/generated/mcuconfig/nested/Delay.c", "SRC/generated/mcuconfig/Delay.c", "src/generated/mcuconfig/延时.c" })
        {
            check(!(bool)outputRule.Invoke(null, [path])!, "generated path boundary rejects: " + path.Replace("\n", "\\n", StringComparison.Ordinal));
        }
        var project = Path.Combine(output, "generated-project");
        await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "generated_modules", project);
        var config = Path.Combine(project, "device.mcuconfig");
        var main = Path.Combine(project, "src/main.c");
        var cmake = Path.Combine(project, "CMakeLists.txt");
        var model = new StcConfigurationModel(await File.ReadAllTextAsync(config));
        model.Apply(JsonSerializer.SerializeToElement(new
        {
            action = "assignPin",
            pinId = "pad-4",
            functionId = "gpio"
        }));
        model.Apply(JsonSerializer.SerializeToElement(new
        {
            action = "parameters",
            values = new Dictionary<string, string>
            {
                ["clockSource"] = "IRC",
                ["clockMHz"] = "24",
                ["systemDivider"] = "2",
                ["reserve"] = "none",
                ["gpioMode"] = "pushPull",
                ["gpioLabel"] = "LED",
                ["gpioLevel"] = "0",
                ["timer0"] = "1",
                ["uartRoute"] = "p36"
            }
        }));
        model.PrepareClockValidation();
        await File.WriteAllTextAsync(config, model.Document.ToJsonString(), new UTF8Encoding(false));
        await File.AppendAllTextAsync(main, "\nvoid user_led_probe(void)\n{\n    LED = 1;\n    LED = 0;\n    LED = !LED;\n}\n\n/* User-owned comment stays byte-for-byte. */\n");
        var beforeMain = await File.ReadAllTextAsync(main);
        var beforeConfig = await File.ReadAllTextAsync(config);
        var beforeCmake = await File.ReadAllTextAsync(cmake);
        await using var services = new WorkbenchService(hostRuntime, Path.Combine(output, "generation-data"));
        var entry = await services.PluginManager.ImportAsync(archive);
        await services.PluginManager.SetEnabledAsync(entry.Id, true);
        await using var broker = new PluginWorkspaceBroker(services, project, new ConfigurationWriteAuthorizer(), () => Task.FromResult(false));
        await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, broker.CallAsync);
        Task<int> Generate(Func<Task<bool>>? dirty = null, CancellationToken token = default) => services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", dirty ?? (() => Task.FromResult(false)), token);
        async Task Reject(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (Exception error) when (error is StudioXException or InvalidOperationException or IOException or OperationCanceledException)
            {
                check(true, name);
                return;
            }
            throw new InvalidOperationException("Expected rejection: " + name);
        }
        var pure = await workspace.InvokeAsync(entry.Id, "command", "interact", JsonSerializer.SerializeToElement(new
        {
            action = "generate"
        }));
        check(pure.GetProperty("files").GetArrayLength() == 10 && await File.ReadAllTextAsync(main) == beforeMain && !Directory.Exists(Path.Combine(project, "src/generated")), "direct plugin invocation returns ten pure files including Delay without writing project");
        await Reject(async () => { await Generate(() => Task.FromResult(true)); }, "dirty editor buffers block code generation");
        await Reject(async () => { await Generate(() => Task.FromResult(File.Exists(Path.Combine(project, "src/generated/mcuconfig/gpio.c")))); }, "interruption during multi-file write triggers rollback");
        check(await File.ReadAllTextAsync(config) == beforeConfig && await File.ReadAllTextAsync(main) == beforeMain && await File.ReadAllTextAsync(cmake) == beforeCmake &&
            !Directory.GetFiles(project, "*.tmp-*", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(project, ".studiox/mcu-generation.pending.json")) &&
            !Directory.GetFiles(Path.Combine(project, "include/generated/mcuconfig")).Any(), "rollback restores all original bytes, removes newly written files, pending marker and temporary files");
        // 模拟进程在写入 main 后退出，使用新服务实例读取持久化恢复记录。
        var operation = Guid.NewGuid().ToString("N");
        var projectKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant())));
        var recoveryDirectory = Path.Combine(services.DataDirectory, "mcu-generation-transactions", projectKey);
        Directory.CreateDirectory(recoveryDirectory);
        var partialMain = beforeMain + "/* interrupted transaction */\n";
        await File.WriteAllBytesAsync(Path.Combine(recoveryDirectory, operation + ".json"), JsonSerializer.SerializeToUtf8Bytes(new[] { new { path = "src/main.c", before = Encoding.UTF8.GetBytes(beforeMain), after = Encoding.UTF8.GetBytes(partialMain) } }, JsonStore.Options));
        var pending = Path.Combine(project, ".studiox/mcu-generation.pending.json");
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
        {
            operationId = operation
        }));
        await File.WriteAllTextAsync(main, partialMain, new UTF8Encoding(false));
        check(!(await services.ProjectClocks.ReadAsync(project)).Ready, "pending generation transaction blocks clock/build readiness");
        var restarted = new McuCodeGenerationService(services.PluginManager, services.Files, services.ProjectClocks, services.DataDirectory);
        await restarted.RecoverAsync(project);
        check(await File.ReadAllTextAsync(main) == beforeMain && !File.Exists(pending), "new application service recovers interrupted transaction from durable backup");
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(new
        {
            operationId = operation
        }));
        await File.WriteAllTextAsync(main, beforeMain + "/* external editor */\n", new UTF8Encoding(false));
        await Reject(() => restarted.RecoverAsync(project), "restart recovery refuses to overwrite external editor changes");
        check(File.Exists(pending) && (await File.ReadAllTextAsync(main)).Contains("external editor", StringComparison.Ordinal), "recovery conflict retains external content, marker and original backup");
        await File.WriteAllTextAsync(main, partialMain, new UTF8Encoding(false));
        await restarted.RecoverAsync(project);
        await File.AppendAllTextAsync(config, " \n");
        await Reject(async () => { await Generate(); }, "configuration disk hash changed after planning is never overwritten");
        await File.WriteAllTextAsync(config, beforeConfig, new UTF8Encoding(false));
        check(await Generate() == 10, "application commits ten module source/header files including Delay");
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        var afterMain = await File.ReadAllTextAsync(main);
        check(afterMain.Contains("#include \"generated/mcuconfig/mcuconfig.h\"", StringComparison.Ordinal) && afterMain.Contains("sx_mcuconfig_init();", StringComparison.Ordinal) &&
            afterMain.EndsWith("/* User-owned comment stays byte-for-byte. */\n", StringComparison.Ordinal), "main includes generated header and calls initialization while preserving user code");
        var gpio = Path.Combine(project, "src/generated/mcuconfig/gpio.c");
        var timer = Path.Combine(project, "src/generated/mcuconfig/timer.c");
        var uart = Path.Combine(project, "src/generated/mcuconfig/uart.c");
        var gpioHeader = await File.ReadAllTextAsync(Path.Combine(project, "include/generated/mcuconfig/gpio.h"));
        check(gpioHeader.Contains("#include \"stc15.h\"", StringComparison.Ordinal) && gpioHeader.Contains("#define LED P10\n", StringComparison.Ordinal) &&
            (await File.ReadAllTextAsync(Path.Combine(project, "include/generated/mcuconfig/mcuconfig.h"))).Contains("#include \"generated/mcuconfig/gpio.h\"", StringComparison.Ordinal),
            "generated entry header exposes the user's direct pin alias to main.c");
        check((await File.ReadAllTextAsync(gpio)).Contains("P1M0", StringComparison.Ordinal) && !(await File.ReadAllTextAsync(gpio)).Contains("TMOD", StringComparison.Ordinal) &&
            (await File.ReadAllTextAsync(timer)).Contains("TH0", StringComparison.Ordinal) && !(await File.ReadAllTextAsync(timer)).Contains("SCON", StringComparison.Ordinal) &&
            (await File.ReadAllTextAsync(uart)).Contains("T2H", StringComparison.Ordinal), "GPIO, timers and UART register writes reside in separate modules");
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(config))!;
        check(receipt["generation"]!["outputs"]!.AsArray().Count == 10 && receipt["generation"]!["generator"]!["version"]!.GetValue<string>() == entry.Version &&
            receipt["generation"]!["inputSha256"]!.GetValue<string>() == ProjectClockService.InputHash(receipt.AsObject()), "generation receipt records actual provider version, content fingerprint and input hash");
        var hashes = Directory.GetFiles(project, "*", SearchOption.AllDirectories).Where(path => !path.Contains("/.build/", StringComparison.Ordinal)).ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check(hashes.All(item => item.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Key)))) && (await File.ReadAllTextAsync(main)).Split("sx_mcuconfig_init();").Length == 2,
            "identical regeneration changes no project bytes and adds no duplicate init or source entries");
        await File.WriteAllTextAsync(main, afterMain.Replace("    sx_mcuconfig_init();", "    if (!sx_mcuconfig_init()) { for (;;) { } }", StringComparison.Ordinal));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check(await File.ReadAllTextAsync(main) == afterMain, "previous generated halt-on-error block migrates to direct initialization call");
        // 用户会直接在初始化调用下一行写业务代码；重生成只确认入口，不拥有这些后续语句。
        var userMain = afterMain.Replace("    sx_mcuconfig_init();", "    sx_mcuconfig_init();\n    /* 用户启动逻辑 */\n    Delay_ms(1);", StringComparison.Ordinal);
        await File.WriteAllTextAsync(main, userMain, new UTF8Encoding(false));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check(await File.ReadAllTextAsync(main) == userMain, "regeneration preserves user statements inside initialization markers");
        var mixedMain = userMain.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("    Delay_ms(1);\r\n", "    Delay_ms(1);\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(main, mixedMain, new UTF8Encoding(true));
        var mixedBytes = await File.ReadAllBytesAsync(main);
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check((await File.ReadAllBytesAsync(main)).SequenceEqual(mixedBytes), "preserved initialization block keeps UTF-8 BOM and even mixed user line endings byte-for-byte");
        var commentMain = userMain + "\n/* Previous form:    if (!sx_mcuconfig_init()) { for (;;) { } } */\n";
        await File.WriteAllTextAsync(main, commentMain, new UTF8Encoding(false));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check(await File.ReadAllTextAsync(main) == commentMain, "old initialization text elsewhere cannot change how the current block is recognized");
        foreach (var broken in new[]
        {
            userMain.Replace("    sx_mcuconfig_init();", "    // sx_mcuconfig_init();", StringComparison.Ordinal),
            userMain.Replace("    sx_mcuconfig_init();", "    if (0) sx_mcuconfig_init();", StringComparison.Ordinal),
            userMain.Replace("    sx_mcuconfig_init();", "    sx_mcuconfig_init(); extra_on_same_line();", StringComparison.Ordinal),
            userMain.Replace("    /* StudioX MCU init end */", "", StringComparison.Ordinal),
            userMain + "\n    /* StudioX MCU init begin */\n",
            userMain + "\n    /* StudioX MCU init end */\n"
        })
        {
            await File.WriteAllTextAsync(main, broken, new UTF8Encoding(false));
            var configSnapshot = await File.ReadAllBytesAsync(config);
            await Reject(async () => { await Generate(); }, "user suffix does not bypass missing, duplicate or modified initialization checks");
            check(await File.ReadAllTextAsync(main) == broken && (await File.ReadAllBytesAsync(config)).SequenceEqual(configSnapshot), "rejected initialization edit preserves main and generation receipt");
        }
        var crlfMain = afterMain.Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(main, crlfMain, new UTF8Encoding(true));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check((await File.ReadAllBytesAsync(main)).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && await File.ReadAllTextAsync(main) == crlfMain, "generation preserves main UTF-8 BOM, CRLF and all user text");
        await File.WriteAllTextAsync(main, afterMain, new UTF8Encoding(false));
        var builder = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets"))) { ProjectClocks = services.ProjectClocks };
        var report = await builder.BuildAsync(project);
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Log);
        }
        File.Copy(report.LogPath!, Path.Combine(output, "generated-modules-build.log"));
        var map = await File.ReadAllTextAsync(Path.Combine(project, ".build/firmware.map"));
        check(map.Contains("_sx_gpio_init", StringComparison.Ordinal) && map.Contains("_sx_timer_init", StringComparison.Ordinal) && map.Contains("_sx_uart_init", StringComparison.Ordinal),
            "actual SDCC compile and link include all three generated peripheral modules");
        var assemblyPath = Directory.GetFiles(Path.Combine(project, ".build"), "main.c.asm", SearchOption.AllDirectories).Single();
        var assembly = await File.ReadAllTextAsync(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(output, "pin-alias-main.asm"));
        check(map.Contains("_user_led_probe", StringComparison.Ordinal) && assembly.Contains("setb\t_P10", StringComparison.Ordinal) && assembly.Contains("clr\t_P10", StringComparison.Ordinal) && assembly.Contains("cpl\t_P10", StringComparison.Ordinal),
            "main reads, writes and toggles LED as the actual P1.0 bit in SDCC assembly");
        var originalGpio = await File.ReadAllTextAsync(gpio);
        await File.AppendAllTextAsync(gpio, "/* handwritten edit */\n");
        check(!(await services.ProjectClocks.ReadAsync(project)).Ready, "modified generated output invalidates clock/build readiness");
        await Reject(async () => { await Generate(); }, "regeneration never overwrites edited generated files");
        check((await File.ReadAllTextAsync(gpio)).EndsWith("/* handwritten edit */\n", StringComparison.Ordinal) && await File.ReadAllTextAsync(main) == afterMain, "conflict leaves edited output and main unchanged");
        await File.WriteAllTextAsync(gpio, originalGpio, new UTF8Encoding(false));
        File.SetAttributes(timer, File.GetAttributes(timer) | FileAttributes.ReadOnly);
        try
        {
            await Reject(async () => { await Generate(); }, "read-only generated file blocks entire operation before writes");
        }
        finally { File.SetAttributes(timer, File.GetAttributes(timer) & ~FileAttributes.ReadOnly); }
        await File.WriteAllTextAsync(main, afterMain.Replace("sx_mcuconfig_init();", "if (1)"));
        await Reject(async () => { await Generate(); }, "modified main integration block is preserved and reported as conflict");
        await File.WriteAllTextAsync(main, afterMain, new UTF8Encoding(false));
        var validCmake = await File.ReadAllTextAsync(cmake);
        await File.WriteAllTextAsync(cmake, "if(TRUE)\n" + validCmake + "\nendif()\n");
        await Reject(async () => { await Generate(); }, "conditional CMake source registration is rejected before any writes");
        await File.WriteAllTextAsync(cmake, validCmake, new UTF8Encoding(false));
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Reject(async () => { await Generate(token: canceled.Token); }, "canceled generation cannot write files");
        }
        await workspace.InvokeAsync(entry.Id, "command", "interact", JsonSerializer.SerializeToElement(new
        {
            action = "parameters",
            values = new Dictionary<string, string> { ["timer0"] = "off", ["uartRoute"] = "off" }
        }));
        await Generate();
        await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
        check(!(await File.ReadAllTextAsync(timer)).Contains("TH0", StringComparison.Ordinal) && !(await File.ReadAllTextAsync(uart)).Contains("T2H", StringComparison.Ordinal), "disabling peripherals removes old register initialization on regeneration");
        report = await builder.BuildAsync(project);
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Log);
        }
        check(true, "regenerated GPIO-only modules actually compile and link");
    }
}

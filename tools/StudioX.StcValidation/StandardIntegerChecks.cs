using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class StandardIntegerChecks
{
    internal static async Task RunAsync(string runtime, string archive, string output)
    {
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("标准整数验收输出目录必须尚不存在。");
        }
        Directory.CreateDirectory(output);
        var rows = new List<CheckRow>();
        var assertions = CreateAssertions();
        var repository = new PackRepository(Path.Combine(output, "repository"));
        var pack = await repository.ImportAsync(archive);
        if (pack.Manifest.Id != "stc.stc8" || pack.Manifest.Devices.Count != 25 ||
            pack.Manifest.Devices.Any(device => device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc" ||
                !device.Templates.Any(template => template.Id == "bare")))
        {
            throw new InvalidOperationException("验收要求完整的 25 型号 STC 8 位 SDCC 器件包及裸机模板。");
        }
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(catalog);
        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archive)));

        async Task SaveAsync()
        {
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                passed = rows.Count == 25 + pack.Manifest.Devices.Sum(device => device.Templates.Count(template => template.Id == "rt51")) + 5 &&
                    rows.All(row => row.Passed),
                hardwareConnected = false,
                packId = pack.Manifest.Id,
                packVersion = pack.Manifest.Version,
                packContentHash = pack.ContentHash,
                archiveSha256 = archiveHash,
                assertionCount = assertions.Count,
                checks = rows
            });
        }

        foreach (var device in pack.Manifest.Devices)
        {
            foreach (var template in device.Templates.Where(template => template.Id is "bare" or "rt51"))
            {
                var root = Path.Combine(output, "projects", device.Id, template.Id);
                var logPath = Path.Combine(output, device.Id + "-" + template.Id + ".log");
                try
                {
                    await new ProjectService().CreateAsync(pack, device.Id, template.Id, "StandardIntegers", root);
                    var main = Path.Combine(root, "src", "main.c");
                    var original = await File.ReadAllTextAsync(main);
                    if (!original.Contains("#include <stdint.h>", StringComparison.Ordinal) ||
                        !original.Contains("while (1)", StringComparison.Ordinal) ||
                        template.Id == "bare" && !original.Contains("uint16_t app_counter", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("模板缺少显式标准整数头、统一无限循环或标准计数类型。");
                    }
                    await File.AppendAllTextAsync(main, "\n" + assertions.Text);
                    if (template.Id == "rt51")
                    {
                        // 仅保存离线编译所需的名义时钟；不创建下载服务或打开串口。
                        await JsonStore.WriteAsync(Path.Combine(root, StcIspSettings.RelativePath),
                            new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200));
                    }
                    var report = await builder.BuildAsync(root);
                    await File.WriteAllTextAsync(logPath, report.Log);
                    if (!report.Success || report.TimedOut ||
                        !File.Exists(Path.Combine(root, ".build", "firmware.ihx")) ||
                        !File.Exists(Path.Combine(root, ".build", "firmware.hex")) ||
                        !File.Exists(Path.Combine(root, ".build", "studiox-build-receipt.json")))
                    {
                        throw new InvalidOperationException("真实 CMake / Ninja / SDCC 编译或产物检查失败。\n" + report.Log);
                    }
                    if (!File.ReadAllBytes(Path.Combine(root, ".build", "firmware.ihx")).AsSpan()
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(root, ".build", "firmware.hex"))))
                    {
                        throw new InvalidOperationException("HEX 副本与实际 SDCC IHX 产物不一致。");
                    }
                    rows.Add(new(device.Id, template.Id, "CMake+Ninja+SDCC", true, logPath, null));
                    Console.WriteLine($"PASS standard integers: {device.Id} / {template.Id}");
                }
                catch (Exception error)
                {
                    await File.AppendAllTextAsync(logPath, "\n" + error);
                    rows.Add(new(device.Id, template.Id, "CMake+Ninja+SDCC", false, logPath, error.ToString()));
                    Console.Error.WriteLine($"FAIL standard integers: {device.Id} / {template.Id}: {error.Message}");
                }
                await SaveAsync();
            }
        }

        foreach (var family in new[] { "stc89", "stc12", "stc15", "stc8g", "stc8h" })
        {
            var root = Path.Combine(output, "header-only", family);
            Directory.CreateDirectory(root);
            var logPath = Path.Combine(root, "sdcc.log");
            try
            {
                var device = pack.Manifest.Devices.First(device => device.Defines.Contains("STUDIOX_" + family.ToUpperInvariant()));
                var tools = await catalog.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
                using var lease = ToolUsageLease.Acquire(tools.RootDirectory);
                var source = Path.Combine(root, "probe.c");
                // 故意只包含型号头；证明类型来自其传递包含的编译器标准头，而不是模板或自造 typedef。
                await File.WriteAllTextAsync(source, "#include \"" + family + ".h\"\n" + assertions.Text +
                    "\nvolatile __xdata uint8_t sx_header_probe;\n");
                string[] arguments = [.. device.CpuFlags, "-I" + Path.Combine(pack.RootDirectory, "sdk", "include"),
                    "-I" + Path.Combine(tools.RootDirectory, "sdcc", "include"), "-c", source,
                    "-o", Path.Combine(root, "probe.rel")];
                var result = await new ProcessRunner().RunAsync(new ProcessRequest(tools.Tool("sdcc"), arguments, root,
                    TimeSpan.FromMinutes(2), ToolsetEnvironment.Create(tools),
                    RemoveEnvironment: [.. ToolsetEnvironment.AmbientVariables, "SDCC_HOME", "SDCC_INCLUDE", "SDCC_LIB", "SDCC_BIN", "SDCCLIB", "SDCCDIR"]));
                await File.WriteAllTextAsync(logPath, tools.Tool("sdcc") + "\n" + string.Join("\n", arguments) +
                    $"\nexit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}\n" +
                    result.StandardOutput + "\n" + result.StandardError);
                if (!result.Success || result.OutputTruncated || !File.Exists(Path.Combine(root, "probe.rel")))
                {
                    throw new InvalidOperationException("型号头独立 SDCC 编译失败，原始输出见 " + logPath);
                }
                rows.Add(new(family, "header-only", "SDCC -c", true, logPath, null));
                Console.WriteLine("PASS transitive standard integers: " + family);
            }
            catch (Exception error)
            {
                await File.AppendAllTextAsync(logPath, "\n" + error);
                rows.Add(new(family, "header-only", "SDCC -c", false, logPath, error.ToString()));
                Console.Error.WriteLine("FAIL transitive standard integers: " + family + ": " + error.Message);
            }
            await SaveAsync();
        }
        if (rows.Any(row => !row.Passed))
        {
            throw new InvalidOperationException("标准整数离线验收失败，完整矩阵及原始诊断已保存到 " + Path.Combine(output, "result.json"));
        }
    }

    private static (string Text, int Count) CreateAssertions()
    {
        var text = new StringBuilder("/* 编译期检查标准整数宽度、符号和宏，不修改模板的运行行为。 */\n");
        var count = 0;
        void Assert(string name, string expression)
        {
            text.Append("typedef char sx_stdint_").Append(name).Append("[(").Append(expression).Append(") ? 1 : -1];\n");
            count++;
        }
        foreach (var bits in new[] { 8, 16, 32, 64 })
        {
            foreach (var prefix in new[] { "", "_least", "_fast" })
            {
                var signed = "int" + prefix + bits + "_t";
                var unsigned = "uint" + prefix + bits + "_t";
                var relation = prefix.Length == 0 ? " == " : " >= ";
                var label = (prefix.Length == 0 ? "exact" : prefix.TrimStart('_')) + bits;
                Assert(label + "_signed_size", "sizeof(" + signed + ")" + relation + bits / 8);
                Assert(label + "_unsigned_size", "sizeof(" + unsigned + ")" + relation + bits / 8);
                Assert(label + "_signed", "(" + signed + ")-1 < 0");
                Assert(label + "_unsigned", "(" + unsigned + ")-1 > 0");
                var macroPrefix = prefix.ToUpperInvariant();
                Assert(label + "_minimum", "INT" + macroPrefix + bits + "_MIN < 0");
                Assert(label + "_maximum", "INT" + macroPrefix + bits + "_MAX > 0");
                Assert(label + "_unsigned_maximum", "UINT" + macroPrefix + bits + "_MAX > INT" + macroPrefix + bits + "_MAX");
            }
            Assert("int" + bits + "_constant", "INT" + bits + "_C(1) == 1");
            Assert("uint" + bits + "_constant", "UINT" + bits + "_C(1) == 1");
        }
        Assert("uintptr_width", "sizeof(uintptr_t) >= sizeof(void *)");
        Assert("intptr_width", "sizeof(intptr_t) >= sizeof(void *)");
        Assert("intmax_width", "sizeof(intmax_t) >= sizeof(int32_t)");
        Assert("uintmax_width", "sizeof(uintmax_t) >= sizeof(uint32_t)");
        Assert("intmax_signed", "(intmax_t)-1 < 0");
        Assert("uintmax_unsigned", "(uintmax_t)-1 > 0");
        Assert("intmax_minimum", "INTMAX_MIN <= INT32_MIN");
        Assert("intmax_maximum", "INTMAX_MAX >= INT32_MAX");
        Assert("uintmax_maximum", "UINTMAX_MAX >= UINT32_MAX");
        Assert("intptr_minimum", "INTPTR_MIN < 0");
        Assert("intptr_maximum", "INTPTR_MAX > 0");
        Assert("uintptr_maximum", "UINTPTR_MAX > INTPTR_MAX");
        Assert("intmax_constant", "INTMAX_C(1) == 1");
        Assert("uintmax_constant", "UINTMAX_C(1) == 1");
        return (text.ToString(), count);
    }

    private sealed record CheckRow(string Device, string Template, string Backend, bool Passed, string LogPath, string? Error);
}

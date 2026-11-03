using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class MemoryModelChecks
{
    internal static async Task RunAsync(string runtime, string archive, string output)
    {
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("内存模型验收输出目录必须尚不存在。");
        }
        Directory.CreateDirectory(output);
        var rows = new List<CheckRow>();
        var limits = new List<LimitRow>();
        var checks = new List<string>();
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        if (pack.Manifest.Id != "stc.stc8" || pack.Manifest.Version != "0.1.9")
        {
            throw new InvalidOperationException("验收使用已包含标准整数的 stc.stc8/0.1.9 包。");
        }
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(catalog);
        var viewer = new BuildAssemblyService(catalog);
        var sequence = new[] { SdccMemoryModel.Large, SdccMemoryModel.Small, SdccMemoryModel.Medium,
            SdccMemoryModel.Large, SdccMemoryModel.ProjectDefault };
        var archiveHash = Hash(await File.ReadAllBytesAsync(archive));

        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        async Task SaveAsync(bool complete = false) => await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = complete && rows.Count == 25 && rows.All(row => row.Passed) && limits.Count == 25,
            hardwareConnected = false,
            packId = pack.Manifest.Id,
            packVersion = pack.Manifest.Version,
            packContentHash = pack.ContentHash,
            archiveSha256 = archiveHash,
            checks,
            deviceLimits = limits,
            builds = rows
        });
        async Task RejectAsync(Func<Task> action, string message)
        {
            try
            {
                await action();
                throw new InvalidOperationException("未拒绝：" + message);
            }
            catch (StudioXException error) when (error.Code is "BUILD_SETTINGS" or "BUILD_ASSEMBLY")
            {
                Check(true, message + " [" + error.Code + "]");
            }
        }

        try
        {
            foreach (var device in pack.Manifest.Devices)
            {
                var root = Path.Combine(output, "capabilities", device.Id);
                await new ProjectService().CreateAsync(pack, device.Id, "bare", "MemoryModelCapabilities", root);
                var ram = await builder.ReadStcRamLimitsAsync(root)
                    ?? throw new InvalidOperationException("STC SDCC 工程缺少 RAM 能力：" + device.Id);
                var template = TemplateResolver.Resolve(device, "bare");
                int Declared(string option)
                {
                    var index = template.LinkOptions.ToList().IndexOf(option);
                    return index >= 0 && index + 1 < template.LinkOptions.Count && int.TryParse(template.LinkOptions[index + 1], out var value)
                        ? value : throw new InvalidOperationException("器件包没有明确声明 " + option);
                }
                Check(ram.IdataSizeBytes == Declared("--iram-size") && ram.XdataOrigin == Declared("--xram-loc") &&
                    ram.XdataSizeBytes == Declared("--xram-size"), device.Id + " RAM 上限来自该型号实际模板声明");
                foreach (var model in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium, SdccMemoryModel.Large })
                {
                    var settings = new ProjectBuildSettings(MemoryModel: model, IdataSizeBytes: ram.IdataSizeBytes, XdataSizeBytes: ram.XdataSizeBytes);
                    await builder.SaveSettingsAsync(root, settings);
                    Check(await builder.LoadSettingsAsync(root) == settings, device.Id + " 保存读取 " + model + " 与准确 RAM 上限");
                }
                foreach (var settings in new[]
                {
                    new ProjectBuildSettings(IdataSizeBytes: ram.IdataSizeBytes + 1),
                    new ProjectBuildSettings(XdataSizeBytes: ram.XdataSizeBytes + 1),
                    new ProjectBuildSettings(IdataSizeBytes: 0), new ProjectBuildSettings(XdataSizeBytes: -1)
                })
                {
                    await RejectAsync(() => builder.SaveSettingsAsync(root, settings), device.Id + " 拒绝超限或零值 RAM 配置 IDATA=" +
                        settings.IdataSizeBytes + ", XDATA=" + settings.XdataSizeBytes);
                }
                var zeroXdata = new ProjectBuildSettings(MemoryModel: SdccMemoryModel.Small, XdataSizeBytes: 0);
                await builder.SaveSettingsAsync(root, zeroXdata);
                Check(await builder.LoadSettingsAsync(root) == zeroXdata, device.Id + " 允许明确声明不使用 XDATA");
                limits.Add(new(device.Id, ram.IdataSizeBytes, ram.XdataOrigin, ram.XdataSizeBytes));
                await SaveAsync();
            }
            foreach (var id in new[] { "STC89C52RC", "IAP15L2K61S2", "STC8H8K64U" })
            {
                var device = pack.Manifest.Devices.Single(device => device.Id == id);
                var family = new[] { "stc89", "stc15", "stc8h" }.Single(family =>
                    device.Defines.Contains("STUDIOX_" + family.ToUpperInvariant()));
                var root = Path.Combine(output, "projects", id);
                await new ProjectService().CreateAsync(pack, id, "bare", "MemoryModels", root);
                await File.WriteAllTextAsync(Path.Combine(root, "src", "main.c"), Probe(family));
                var previousStamp = "";
                var previousAssemblyHash = "";
                for (var index = 0; index < sequence.Length; index++)
                {
                    var model = sequence[index];
                    var label = id + "-" + index + "-" + model;
                    var settings = new ProjectBuildSettings(MemoryModel: model);
                    await builder.SaveSettingsAsync(root, settings);
                    Check(await builder.LoadSettingsAsync(root) == settings, label + " 保存后完整往返读取");
                    Check(!File.Exists(Path.Combine(root, ".build", "studiox-build-receipt.json")), label + " 修改模型撤销旧构建凭据");
                    await RejectAsync(async () => { await viewer.ListAsync(root); }, label + " 未重编译前拒绝旧汇编");
                    var row = await BuildAsync(root, id, "bare", label, Effective(model), true);
                    Check(row.SourceStamp != previousStamp, label + " 构建凭据记录本轮设置");
                    if (index is > 0 and < 4)
                    {
                        Check(row.AssemblySha256 != previousAssemblyHash, label + " 切换后实际重新编译并改变寻址");
                    }
                    previousStamp = row.SourceStamp!;
                    previousAssemblyHash = row.AssemblySha256!;
                    await SaveAsync();
                }
                var savedPath = Path.Combine(root, ProjectBuildSettings.RelativePath);
                var saved = await File.ReadAllBytesAsync(savedPath);
                await RejectAsync(() => builder.SaveSettingsAsync(root,
                    new ProjectBuildSettings(MemoryModel: (SdccMemoryModel)999)), id + " 非法模型枚举在持久化前拒绝");
                var afterRejected = await File.ReadAllBytesAsync(savedPath);
                Check(saved.AsSpan().SequenceEqual(afterRejected), id + " 非法配置没有改写有效设置");
                await builder.SaveSettingsAsync(root, new ProjectBuildSettings(MemoryModel: SdccMemoryModel.Large,
                    IdataSizeBytes: 128, XdataSizeBytes: 128));
                await BuildAsync(root, id, "bare-ram-limit", id + "-ram128", "large", true);
                await VerifyRamAsync(root, 128, 128, id + " 实际缩小 RAM");
                await SaveAsync();
            }

            var rtDevice = pack.Manifest.Devices.Single(device => device.Templates.Any(template => template.Id == "rt51"));
            var rtRoot = Path.Combine(output, "projects", rtDevice.Id + "-rt51");
            await new ProjectService().CreateAsync(pack, rtDevice.Id, "rt51", "MemoryModelsRt51", rtRoot);
            await JsonStore.WriteAsync(Path.Combine(rtRoot, StcIspSettings.RelativePath),
                new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200));
            foreach (var model in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium })
            {
                // 原模板的 rt51.h 明确要求 model-large 与 stack-auto，不能静默改写内核 ABI。
                await RejectAsync(() => builder.SaveSettingsAsync(rtRoot, new ProjectBuildSettings(MemoryModel: model)),
                    "RT51 明确拒绝不符合其内核 ABI 的 " + model + " 模型");
            }
            foreach (var model in new[] { SdccMemoryModel.Large, SdccMemoryModel.ProjectDefault })
            {
                await builder.SaveSettingsAsync(rtRoot, new ProjectBuildSettings(MemoryModel: model));
                await BuildAsync(rtRoot, rtDevice.Id, "rt51", "rt51-" + model, Effective(model), false);
                await SaveAsync();
            }
            var monRoot = Path.Combine(output, "projects", "IAP15L2K61S2-mon51");
            await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "MemoryModelsMon51", monRoot);
            await File.WriteAllTextAsync(Path.Combine(monRoot, "src", "main.c"), Probe("stc15"));
            foreach (var model in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium, SdccMemoryModel.Large })
            {
                await builder.SaveSettingsAsync(monRoot, new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard,
                    CodeRomSizeBytes: 0xdbfd, Mon51Profile: true, MemoryModel: model));
                await BuildAsync(monRoot, "IAP15L2K61S2", "Mon51", "mon51-" + model, Effective(model), true);
                Check(File.Exists(Path.Combine(monRoot, ".build", "firmware.cdb")), "Mon51 " + model + " 生成标准源码符号");
                await SaveAsync();
            }
            var monLimits = await builder.ReadStcRamLimitsAsync(monRoot)
                ?? throw new InvalidOperationException("Mon51 工程缺少 RAM 范围。");
            await RejectAsync(() => builder.SaveSettingsAsync(monRoot,
                new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard, CodeRomSizeBytes: 0xdbfd,
                    Mon51Profile: true, XdataSizeBytes: monLimits.MaximumXdataBytes(true) + 1)), "Mon51 不能扩大到监控保留区");
            await builder.SaveSettingsAsync(monRoot, new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard,
                CodeRomSizeBytes: 0xdbfd, Mon51Profile: true, MemoryModel: SdccMemoryModel.Large,
                IdataSizeBytes: 128, XdataSizeBytes: 64));
            await BuildAsync(monRoot, "IAP15L2K61S2", "Mon51-ram-limit", "mon51-ram64", "large", true);
            await VerifyRamAsync(monRoot, 128, 64, "Mon51 用户较小 RAM 配置不被预设扩大");

            var nonSdcc = Path.Combine(output, "projects", "non-sdcc-settings");
            var manifest = await new ProjectService().CreateAsync(pack, "STC89C52RC", "bare", "NonSdccSettings", nonSdcc);
            // 只验证服务的能力边界，不解析或执行虚构的非 SDCC 工具。
            await JsonStore.WriteAsync(Path.Combine(nonSdcc, ".studiox", "project.json"),
                manifest with
                {
                    ToolsetId = "arm.none-eabi",
                    CompilerId = "gcc",
                    DevelopmentComponents = [new("arm.none-eabi", manifest.ToolsetVersion, "gcc")]
                });
            await RejectAsync(() => builder.SaveSettingsAsync(nonSdcc,
                new ProjectBuildSettings(MemoryModel: SdccMemoryModel.Large)), "非 SDCC 工程拒绝 SDCC 模型选项");
            await RejectAsync(() => builder.SaveSettingsAsync(nonSdcc,
                new ProjectBuildSettings(IdataSizeBytes: 128)), "非 SDCC 工程拒绝 SDCC IDATA 选项");
            await RejectAsync(() => builder.SaveSettingsAsync(nonSdcc,
                new ProjectBuildSettings(XdataSizeBytes: 128)), "非 SDCC 工程拒绝 SDCC XDATA 选项");
            Check(!File.Exists(Path.Combine(nonSdcc, ProjectBuildSettings.RelativePath)), "非 SDCC 拒绝后没有生成设置文件");

            var conflict = Path.Combine(output, "projects", "STC8H8K64U");
            await builder.SaveSettingsAsync(conflict, new ProjectBuildSettings(MemoryModel: SdccMemoryModel.Large));
            await File.AppendAllTextAsync(Path.Combine(conflict, "CMakeLists.txt"),
                "\ntarget_compile_options(firmware PRIVATE --model-small)\n");
            try
            {
                var conflictReport = await builder.BuildAsync(conflict);
                await File.WriteAllTextAsync(Path.Combine(output, "conflicting-custom-rule.log"), conflictReport.Log);
                Check(!conflictReport.Success && !conflictReport.TimedOut, "自定义编译规则混入另一内存模型时明确构建失败");
            }
            catch (StudioXException error) when (error.Code == "SDCC_MEMORY_MODEL_CONFLICT")
            {
                await File.WriteAllTextAsync(Path.Combine(output, "conflicting-custom-rule.log"), error.ToString());
                Check(true, "自定义编译规则混入另一内存模型时明确拒绝 [" + error.Code + "]");
            }
            Check(!File.Exists(Path.Combine(conflict, ".build", "studiox-build-receipt.json")), "模型冲突不能保留成功构建凭据");

            var overflow = Path.Combine(output, "projects", "IAP15L2K61S2");
            await File.AppendAllTextAsync(Path.Combine(overflow, "src", "main.c"), "\n__xdata uint8_t sx_excess[129];\n");
            var overflowReport = await builder.BuildAsync(overflow);
            await File.WriteAllTextAsync(Path.Combine(output, "xdata-overflow.log"), overflowReport.Log);
            Check(!overflowReport.Success && !overflowReport.TimedOut, "实际 XDATA 对象超过用户 128 字节链接上限时构建失败");
            Check(!File.Exists(Path.Combine(overflow, ".build", "studiox-build-receipt.json")), "RAM 溢出不能留下成功凭据");

            var zeroRoot = Path.Combine(output, "projects", "STC89C52RC-zero-xdata");
            await new ProjectService().CreateAsync(pack, "STC89C52RC", "bare", "ZeroXdata", zeroRoot);
            await File.WriteAllTextAsync(Path.Combine(zeroRoot, "src", "main.c"), Probe("stc89"));
            await builder.SaveSettingsAsync(zeroRoot, new ProjectBuildSettings(MemoryModel: SdccMemoryModel.Small, XdataSizeBytes: 0));
            await BuildAsync(zeroRoot, "STC89C52RC", "zero-xdata", "zero-xdata", "small", true);
            var zeroMemory = await File.ReadAllTextAsync(Path.Combine(zeroRoot, ".build", "firmware.mem"));
            Check(Regex.IsMatch(zeroMemory, @"(?m)^\s*EXTERNAL RAM\s+0\s+0\s*$"), "纯 DATA 程序实际零 XDATA 链接成功且 .mem 上限为零");
            await File.AppendAllTextAsync(Path.Combine(zeroRoot, "src", "main.c"), "\n__xdata uint8_t sx_not_allowed;\n");
            var zeroOverflow = await builder.BuildAsync(zeroRoot);
            await File.WriteAllTextAsync(Path.Combine(output, "zero-xdata-overflow.log"), zeroOverflow.Log);
            Check(!zeroOverflow.Success && !zeroOverflow.TimedOut, "零 XDATA 配置下出现实际 XDATA 对象时链接失败");
            Check(!File.Exists(Path.Combine(zeroRoot, ".build", "studiox-build-receipt.json")), "零 XDATA 溢出不能留下成功凭据");
            await SaveAsync(true);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), error.ToString());
            await SaveAsync();
            throw;
        }

        async Task VerifyRamAsync(string root, int idata, int xdata, string label)
        {
            var memory = await File.ReadAllTextAsync(Path.Combine(root, ".build", "firmware.mem"));
            var stack = Regex.Match(memory, @"Stack starts at:\s*0x([0-9a-fA-F]+)[^\r\n]*with\s+(\d+)\s+bytes available");
            Check(stack.Success && Convert.ToInt32(stack.Groups[1].Value, 16) + int.Parse(stack.Groups[2].Value) == idata,
                label + " .mem 栈边界对应 IDATA " + idata);
            var external = Regex.Match(memory, @"EXTERNAL RAM\s+0x[0-9a-fA-F]+\s+0x[0-9a-fA-F]+\s+\d+\s+(\d+)");
            Check(external.Success && int.Parse(external.Groups[1].Value) == xdata, label + " .mem 最大 XDATA 为 " + xdata);
            var commands = await File.ReadAllTextAsync(Path.Combine(root, ".build", "studiox-sdcc-commands.txt"));
            var link = commands.Split('\n').Last(line => line.Contains("sdcc", StringComparison.OrdinalIgnoreCase) && line.Contains(".ihx", StringComparison.Ordinal));
            foreach (var (option, expected) in new[] { ("--iram-size", idata), ("--xram-size", xdata) })
            {
                var flags = Regex.Matches(link, Regex.Escape(option) + @"\s+(\d+)");
                Check(flags.Count == 1 && int.Parse(flags[0].Groups[1].Value) == expected, label + " 链接命令唯一应用 " + option);
            }
        }

        async Task<CheckRow> BuildAsync(string root, string device, string template, string label, string model, bool probe)
        {
            var evidence = Path.Combine(output, "evidence", label);
            Directory.CreateDirectory(evidence);
            try
            {
                var report = await builder.BuildAsync(root);
                await File.WriteAllTextAsync(Path.Combine(evidence, "build.log"), report.Log);
                Check(report.Success && !report.TimedOut, label + " 真实 CMake + Ninja + SDCC 编译链接");
                var binary = Path.Combine(root, ".build");
                foreach (var name in new[] { "firmware.ihx", "firmware.hex", "firmware.map", "firmware.mem", "firmware.lk",
                    "compile_commands.json", "studiox-build-receipt.json" })
                {
                    File.Copy(Path.Combine(binary, name), Path.Combine(evidence, name));
                }
                if (File.Exists(Path.Combine(binary, "firmware.cdb")))
                {
                    File.Copy(Path.Combine(binary, "firmware.cdb"), Path.Combine(evidence, "firmware.cdb"));
                }
                var ihx = await File.ReadAllBytesAsync(Path.Combine(binary, "firmware.ihx"));
                var hex = await File.ReadAllBytesAsync(Path.Combine(binary, "firmware.hex"));
                Check(ihx.AsSpan().SequenceEqual(hex), label + " HEX 与实际 IHX 完全一致");
                using var compile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(binary, "compile_commands.json")));
                foreach (var command in compile.RootElement.EnumerateArray())
                {
                    var text = command.TryGetProperty("command", out var raw) ? raw.GetString()! :
                        string.Join(' ', command.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()));
                    RequireModel(text, model, label + " 编译命令");
                }
                var manifest = await ProjectService.ReadAsync(root);
                var tools = await catalog.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId);
                using var lease = ToolUsageLease.Acquire(tools.RootDirectory);
                var commands = await new ProcessRunner().RunAsync(new ProcessRequest(tools.Tool("ninja"), ["-t", "commands", "firmware.ihx"],
                    binary, TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(tools),
                    RemoveEnvironment: [.. ToolsetEnvironment.AmbientVariables, "SDCC_HOME", "SDCC_INCLUDE", "SDCC_LIB", "SDCC_BIN", "SDCCLIB", "SDCCDIR"]));
                await File.WriteAllTextAsync(Path.Combine(evidence, "ninja-commands.log"), commands.StandardOutput + "\n" + commands.StandardError);
                Check(commands.Success && !commands.OutputTruncated, label + " 获取实际 Ninja 编译链接命令");
                var link = commands.StandardOutput.Split('\n').Last(line => line.Contains("sdcc", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains(".ihx", StringComparison.OrdinalIgnoreCase));
                RequireModel(link, model, label + " 链接命令");
                var map = (await File.ReadAllTextAsync(Path.Combine(binary, "firmware.map"))).Replace('\\', '/');
                var expectedLibrary = model + (compile.RootElement.EnumerateArray().Any(command =>
                    command.ToString().Contains("--stack-auto", StringComparison.Ordinal)) ? "-stack-auto" : "");
                var libraries = Regex.Matches(map, @"(?im)^.*?/lib/((?:small|medium|large)(?:-stack-auto)?)/[^\r\n]+\.lib\s*$");
                Check(libraries.Count > 0 && libraries.All(match => match.Groups[1].Value == expectedLibrary),
                    label + " 实际解析全部模型和调用约定匹配的 " + expectedLibrary + " 运行库");
                var assembly = Directory.GetFiles(binary, "*.asm", SearchOption.AllDirectories)
                    .Single(path => Path.GetFileName(path) == "main.c.asm");
                var textAssembly = await File.ReadAllTextAsync(assembly);
                File.Copy(assembly, Path.Combine(evidence, "main.asm"));
                RequireModel(Regex.Match(textAssembly, @"(?m)^\s*\.optsdcc[^\r\n]*").Value, model, label + " SDCC 汇编模型声明");
                if (probe)
                {
                    var area = "";
                    foreach (var line in textAssembly.Split('\n'))
                    {
                        var areaMatch = Regex.Match(line, @"^\s*\.area\s+(\w+)");
                        if (areaMatch.Success)
                        {
                            area = areaMatch.Groups[1].Value;
                        }
                        if (Regex.IsMatch(line, @"^_sx_default_byte::?\s*$"))
                        {
                            break;
                        }
                    }
                    var expected = model switch
                    {
                        "small" => "DSEG",
                        "medium" => "PSEG",
                        _ => "XSEG"
                    };
                    Check(area == expected, label + " 未加地址空间的变量实际位于 " + expected);
                    var body = textAssembly[textAssembly.IndexOf("_sx_touch:", StringComparison.Ordinal)..];
                    Check(model switch
                    {
                        "small" => Regex.IsMatch(body, @"(?m)^\s*(?:mov|inc)\s+[^;\r\n]*_sx_default_byte"),
                        "medium" => Regex.IsMatch(body, @"(?m)^\s*mov\s+r0,#_sx_default_byte") && Regex.IsMatch(body, @"(?m)^\s*movx\s+a,@r0"),
                        _ => Regex.IsMatch(body, @"(?m)^\s*mov\s+dptr,#_sx_default_byte") && Regex.IsMatch(body, @"(?m)^\s*movx\s+a,@dptr")
                    }, label + " 实际生成该地址空间的访问指令");
                    Check(map.Contains("_divulong", StringComparison.Ordinal), label + " 32 位除法实际链接运行库辅助函数");
                }
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(binary, "studiox-build-receipt.json")));
                var row = new CheckRow(device, template, label, model, true, evidence,
                    receipt.RootElement.GetProperty("sourceStamp").GetString(), Hash(await File.ReadAllBytesAsync(assembly)),
                    Hash(await File.ReadAllBytesAsync(Path.Combine(binary, "firmware.hex"))), null);
                rows.Add(row);
                return row;
            }
            catch (Exception error)
            {
                rows.Add(new(device, template, label, model, false, evidence, null, null, null, error.ToString()));
                throw;
            }
        }
    }

    private static void RequireModel(string command, string expected, string label)
    {
        var models = Regex.Matches(command, @"--model-(small|medium|large|huge)\b");
        if (models.Count != 1 || models[0].Groups[1].Value != expected)
        {
            throw new InvalidOperationException(label + " 必须且仅能使用 --model-" + expected + "：\n" + command);
        }
    }

    private static string Effective(SdccMemoryModel model) => model switch
    {
        SdccMemoryModel.Small => "small",
        SdccMemoryModel.Medium => "medium",
        _ => "large"
    };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Probe(string family) => "#include <stdint.h>\n#include \"" + family + ".h\"\n" + """

        /* 不指定地址空间，核对模型实际改变的数据分配和寻址；不操作外设。 */
        volatile uint8_t sx_default_byte;
        volatile uint32_t sx_default_wide;
        volatile uint32_t sx_denominator = 3;
        void sx_touch(void)
        {
            ++sx_default_byte;
            sx_default_wide = (sx_default_wide * 257UL) / sx_denominator;
        }
        void main(void)
        {
            while (1)
            {
                sx_touch();
            }
        }
        """;
    private sealed record CheckRow(string Device, string Template, string Label, string EffectiveModel, bool Passed,
        string EvidenceDirectory, string? SourceStamp, string? AssemblySha256, string? HexSha256, string? Error);
    private sealed record LimitRow(string Device, int IdataSizeBytes, int XdataOrigin, int XdataSizeBytes);
}

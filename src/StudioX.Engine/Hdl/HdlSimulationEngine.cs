namespace StudioX.Engine.Hdl;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>用内置 Icarus 执行有时间上限的 RTL testbench，输出独立 VCD 与完整诊断。</summary>
public sealed class HdlSimulationEngine(ToolsetCatalog catalog)
{
    public async Task CreateTestbenchAsync(string root, string relative, string top, CancellationToken token = default)
    {
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        if (!Regex.IsMatch(top, @"\A[A-Za-z_][A-Za-z0-9_$]*\z") || Path.GetExtension(relative) is not (".v" or ".sv"))
        {
            throw new StudioXException("HDL_SIM_BENCH", "请填写合法 testbench 顶层模块及 .v/.sv 工程相对路径。");
        }
        var path = PathBoundary.Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 只创建新文件，模板中的失败断言防止未连接 DUT 的空测试被误认为通过。
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(($"`timescale 1ns/1ps\nmodule {top};\n" +
            "  // 在此声明激励信号，实例化待测模块，并连接端口。\n" +
            "  // 使用 # 延迟驱动时钟、复位与数据，用 $fatal 检查实际输出。\n" +
            "  // StudioX 自动生成 VCD；完成所有断言后调用 $finish。\n" +
            "  initial begin\n    #1;\n    $fatal(1, \"TODO: connect DUT and add assertions\");\n  end\nendmodule\n").AsMemory(), token);
    }

    public async Task<bool> IsCurrentAsync(HdlSimulationResult result, CancellationToken token = default)
    {
        var hashes = result.Inputs.Where(pair => pair.Key != HdlSimulationSettings.RelativePath).ToDictionary();
        var settingsPath = PathBoundary.Resolve(result.ProjectDirectory, HdlSimulationSettings.RelativePath);
        return await HdlSchematicInputs.IsCurrentAsync(result.ProjectDirectory, hashes, result.Settings.Inputs, token) &&
            (File.Exists(settingsPath) ? await Ag32NativeBuildService.HashAsync(settingsPath, token) : "") == result.Inputs[HdlSimulationSettings.RelativePath];
    }
    public async Task<HdlSimulationSettings> ReadSettingsAsync(string root, CancellationToken token = default)
    {
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        var path = PathBoundary.Resolve(root, HdlSimulationSettings.RelativePath);
        if (File.Exists(path))
        {
            return await JsonStore.ReadAsync<HdlSimulationSettings>(path, token);
        }
        var logic = await new Ag32NativeBuildService(catalog).ReadSettingsAsync(root, token);
        return new(1, logic.Sources, logic.IncludeDirectories, logic.Defines, "sim/tb_user_logic.v", "tb_user_logic");
    }

    public async Task SaveSettingsAsync(string root, HdlSimulationSettings settings, CancellationToken token = default)
    {
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        Validate(root, settings);
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, HdlSimulationSettings.RelativePath), settings, token);
    }

    private static void Validate(string root, HdlSimulationSettings settings)
    {
        if (settings.FormatVersion != 1 || settings.Sources is null || settings.IncludeDirectories is null || settings.Defines is null ||
            string.IsNullOrWhiteSpace(settings.TestbenchFile) || string.IsNullOrWhiteSpace(settings.TestbenchTop) ||
            settings.DurationNanoseconds is < 1 or > 1000000000 || settings.TimeoutSeconds is < 1 or > 300)
        {
            throw new StudioXException("HDL_SIM_SETTINGS", "请指定 testbench 顶层、1–1,000,000,000 ns 仿真时间及 1–300 秒超时。");
        }
        HdlSchematicInputs.Validate(root, settings.Inputs);
    }

    public Task<HdlSimulationResult> RunAsync(string projectDirectory, HdlSimulationSettings settings, CancellationToken token = default,
        IProgress<string>? progress = null, IProgress<string>? output = null) =>
        Task.Run(() => RunCoreAsync(Path.GetFullPath(projectDirectory), settings, token, progress, output), token);

    private async Task<HdlSimulationResult> RunCoreAsync(string root, HdlSimulationSettings settings, CancellationToken token,
        IProgress<string>? progress, IProgress<string>? output)
    {
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        Validate(root, settings);
        progress?.Report("校验 RTL 仿真工具…");
        var tools = await catalog.ResolveAsync("hdl.iverilog", "14.0.0", "iverilog", token, progress: progress);
        var run = PathBoundary.Resolve(root, ".build/hdl-simulation/" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(run, "source");
        Directory.CreateDirectory(source);
        var hashes = await HdlSchematicInputs.SnapshotAsync(root, source, settings.Inputs, token);
        var settingsPath = PathBoundary.Resolve(root, HdlSimulationSettings.RelativePath);
        hashes[HdlSimulationSettings.RelativePath] = File.Exists(settingsPath) ? await Ag32NativeBuildService.HashAsync(settingsPath, token) : "";
        var logPath = Path.Combine(run, "simulation.log");
        var capture = "`timescale 1ns/1ps\nmodule studiox_capture;\ninitial begin\n$dumpfile(\"wave.vcd\");\n$dumpvars(0, " +
            settings.TestbenchTop + ");\n$dumplimit(33554432);\n#" + settings.DurationNanoseconds.ToString(CultureInfo.InvariantCulture) +
            ";\n$display(\"STUDIOX_SIMULATION_TIME_LIMIT\");\n$finish;\nend\nendmodule\n";
        await File.WriteAllTextAsync(Path.Combine(run, "capture.v"), capture, token);
        var library = IcarusToolPath.ForLibraryDirectory(tools.ResourceDirectory("ivl"));
        var arguments = new List<string> { "-B", library, "-g2012", "-gspecify", "-Wall", "-s", settings.TestbenchTop,
            "-s", "studiox_capture", "-o", "simulation.vvp" };
        foreach (var directory in settings.IncludeDirectories)
        {
            arguments.AddRange(["-I", directory == "." ? "source" : "source/" + directory]);
        }
        foreach (var define in settings.Defines)
        {
            arguments.AddRange(["-D", define]);
        }
        arguments.AddRange(settings.Inputs.Sources.Select(file => "source/" + file));
        arguments.Add("capture.v");
        await ExecuteAsync(tools.Tool("iverilog"), arguments, "编译 testbench");
        await ExecuteAsync(tools.Tool("vvp"), ["-M", library, "simulation.vvp"], "RTL 事件仿真");
        var current = hashes.Where(pair => pair.Key != HdlSimulationSettings.RelativePath).ToDictionary();
        if (!await HdlSchematicInputs.IsCurrentAsync(root, current, settings.Inputs, token) ||
            (File.Exists(settingsPath) ? await Ag32NativeBuildService.HashAsync(settingsPath, token) : "") != hashes[HdlSimulationSettings.RelativePath])
        {
            throw new StudioXException("HDL_SIM_CHANGED", "仿真期间源码或设置发生变化，结果只保留为历史记录：" + run);
        }
        var vcd = Path.Combine(run, "wave.vcd");
        if (!File.Exists(vcd))
        {
            throw new StudioXException("HDL_SIM_WAVE", "testbench 未产生预期 VCD；请勿覆盖自动记录器的 $dumpfile。日志：" + logPath);
        }
        var lines = await File.ReadAllLinesAsync(logPath, token);
        if (lines.Any(line => line.Contains("Dump file limit", StringComparison.OrdinalIgnoreCase)))
        {
            throw new StudioXException("HDL_SIM_WAVE_LIMIT", "VCD 已达到 32 MiB 上限，请缩短仿真时间。原始日志：" + logPath);
        }
        var warnings = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var message = lines[index];
            while (index + 1 < lines.Length && lines[index + 1].Length > 0 && char.IsWhiteSpace(lines[index + 1][0]))
            {
                message += "\n" + lines[++index];
            }
            warnings.Add(message);
        }
        if (lines.Any(line => line.Contains("STUDIOX_SIMULATION_TIME_LIMIT", StringComparison.Ordinal)))
        {
            warnings.Add("已达到仿真时间上限；这不代表 testbench 中的全部断言已执行。");
        }
        var report = new HdlSimulationResult(root, settings, VcdReader.Read(vcd), vcd, logPath, hashes, tools.Fingerprint, warnings.ToArray());
        // VCD 是事件的唯一持久副本，索引只存摘要，避免端口别名让 JSON 成倍膨胀。
        await JsonStore.WriteAsync(Path.Combine(run, "result.json"), new
        {
            report.ProjectDirectory,
            report.Settings,
            report.VcdPath,
            report.LogPath,
            report.Inputs,
            report.ToolFingerprint,
            report.Warnings,
            report.Waveform.NanosecondsPerTick,
            report.Waveform.EndTick,
            signals = report.Waveform.Signals.Select(signal => new { signal.Name, signal.Width, changes = signal.Changes.Length })
        }, token);
        return report;

        async Task ExecuteAsync(string executable, IReadOnlyList<string> args, string phase)
        {
            progress?.Report(phase);
            using var writer = new StreamWriter(logPath, true) { AutoFlush = true };
            writer.WriteLine("[" + phase + "]");
            var result = await new ProcessRunner().RunAsync(new(executable, args, run, TimeSpan.FromSeconds(settings.TimeoutSeconds),
                ToolsetEnvironment.Create(tools), RemoveEnvironment: ["IVERILOG_ICONFIG", "IVERILOG_VPI_MODULE_PATH", "VVP_DUMPER"],
                Output: new LogProgress(writer, output), StreamCompleteOutput: true), token);
            writer.WriteLine($"exit={result.ExitCode}; timeout={result.TimedOut}");
            if (!result.Success)
            {
                throw new StudioXException("HDL_SIM_FAILED", phase + $"失败（退出码 {result.ExitCode}，超时 {result.TimedOut}）。原始日志：" + logPath + "\n" + result.StandardError + "\n" + result.StandardOutput);
            }
        }
    }

    private sealed class LogProgress(StreamWriter writer, IProgress<string>? output) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (writer)
            {
                writer.WriteLine(value);
            }
            output?.Report(value);
        }
    }
}

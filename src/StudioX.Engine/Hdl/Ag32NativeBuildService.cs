namespace StudioX.Engine.Hdl;

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>将用户逻辑、VE 与时序约束快照交给内置原生工具，生成独立的 FPGA 镜像。</summary>
public sealed partial class Ag32NativeBuildService(ToolsetCatalog catalog, string? licenseDirectory = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public Task<BuildReport> BuildAsync(string projectDirectory, IProgress<string>? progress = null,
        CancellationToken token = default, IProgress<string>? output = null) =>
        Task.Run(() => BuildCoreAsync(Path.GetFullPath(projectDirectory), progress, output, token), token);

    private async Task<BuildReport> BuildCoreAsync(string root, IProgress<string>? progress, IProgress<string>? output, CancellationToken token)
    {
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("AG32_LOGIC_BUSY", "自定义逻辑正在构建。");
        }
        var run = PathBoundary.Resolve(root, ".build/ag32-logic/" + Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(run, "build.log");
        var log = new StringBuilder();
        var licensing = new Ag32PinMappingBuildService(catalog, licenseDirectory);
        string? privateRuntime = null;
        try
        {
            Invalidate(root);
            Directory.CreateDirectory(run);
            var project = await HdlSchematicInputs.RequireProjectAsync(root, token);
            await RequireDeviceAsync(root, project, token);
            var settings = await ReadSettingsAsync(root, token);
            ValidateSettings(root, settings);
            var mapping = project.PinMapping!;
            var logicTools = project.Logic!;
            var tools = await catalog.ResolveAsync(mapping.ToolsetId, mapping.ToolsetVersion, mapping.CompilerId, token, progress: progress);
            var native = await catalog.ResolveAsync(logicTools.ToolsetId, logicTools.ToolsetVersion, logicTools.CompilerId, token, progress: progress);
            var toolLock = new ToolchainLock(1, logicTools.ToolsetId, logicTools.ToolsetVersion, native.Fingerprint);
            var lockPath = PathBoundary.Resolve(root, ToolLockPath);
            if (File.Exists(lockPath))
            {
                var previous = await JsonStore.ReadAsync<ToolchainLock>(lockPath, token);
                // 旧版将映射与综合两项指纹串在同一锁中；两项都吻合时才转换为单组件锁。
                if (previous != toolLock && previous != toolLock with
                {
                    Fingerprint = tools.Fingerprint + ":" + native.Fingerprint
                })
                {
                    throw new StudioXException("TOOLCHAIN_LOCK", "自定义逻辑工具与工程锁定不一致。");
                }
            }
            await JsonStore.WriteAsync(lockPath, toolLock, token);
            var source = Path.Combine(run, "source");
            Directory.CreateDirectory(source);
            var hashes = await HdlSchematicInputs.SnapshotAsync(root, source, settings.Inputs, token);
            foreach (var relative in settings.SdcFiles.Prepend("logic/pins.ve").Append(Ag32NativeBuildSettings.RelativePath).Append(ToolLockPath))
            {
                var original = PathBoundary.Resolve(root, relative);
                hashes[relative] = File.Exists(original) ? await HashAsync(original, token) : "";
                if (File.Exists(original))
                {
                    var destination = PathBoundary.Resolve(source, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(original, destination, true);
                }
            }
            File.Copy(Path.Combine(source, "logic/pins.ve"), Path.Combine(run, "pins.ve"));
            var environment = ToolsetEnvironment.Create(tools);
            await RunAsync(tools.Tool("python"), ["-I", "-B", tools.Tool("converter"), "-d", project.Logic!.TargetDevice,
                "-s", "-m", "source/logic/user_logic.v", "-c", "pins.hx", "pins.ve", "pins.v", "-x", "pins.vex"], "VE 接口生成", environment);

            var includes = HdlSchematicInputs.PrepareIncludes(source, settings.Inputs, hashes.Keys.Where(key => key != Ag32NativeBuildSettings.RelativePath), token);
            var mapScript = await File.ReadAllTextAsync(Path.Combine(tools.ResourceDirectory("supra"), "etc/af_map.tcl"), token);
            mapScript = mapScript.Replace("__design_name__", "pins").Replace("__top_module__", "pins")
                .Replace("__lib_dirs__", "").Replace("__verilog_files__", "pins.v").Replace("__project_dir__", ".")
                .Replace("read_verilog -sv -overwrite", "read_verilog -sv -overwrite " +
                    string.Join(" ", includes.Keys.Select((_, index) => "-Iincludes/" + index)) + " " +
                    string.Join(" ", settings.Defines.Select(define => "-D" + define)));
            await File.WriteAllTextAsync(Path.Combine(run, "map.tcl"), mapScript, token);
            await RunAsync(native.Tool("mapper"), new[] { "-TQ", "-L", "mapper.log", "-c", "map.tcl", "pins.v" }
                .Concat(settings.Sources.Select(file => "source/" + file)).ToArray(), "Verilog 原生综合", ToolsetEnvironment.Create(native));
            await Ag32PinMappingClockVerification.CreateSdcAsync(run, token);
            var veSource = await File.ReadAllBytesAsync(Path.Combine(run, "pins.ve"), token);
            await Ag32GpioElectrical.CreateAsync(run, veSource, token);
            foreach (var file in settings.SdcFiles)
            {
                await File.AppendAllTextAsync(Path.Combine(run, "studiox-clocks.sdc"), "\n" + await File.ReadAllTextAsync(PathBoundary.Resolve(source, file), token), token);
            }
            privateRuntime = await licensing.PreparePrivateRuntimeAsync(tools, token);
            environment["ALTA_HOME"] = privateRuntime.Replace('\\', '/');
            await File.WriteAllTextAsync(Path.Combine(run, "route.tcl"), Ag32NativeScripts.PlaceAndRoute, token);
            await RunAsync(tools.Tool("supra"), ["-L", "supra.log", "-X", "set DEVICE " + project.Logic.TargetDevice, "-F", "route.tcl"], "布局布线与位流", environment);
            // Supra 某些失败仍返回 0；检查完整厂商日志，不能只依赖进程退出码或截断输出。
            var diagnostic = await File.ReadAllTextAsync(Path.Combine(run, "supra.log"), token);
            if (Regex.IsMatch(diagnostic, @"(?im)^\s*(Error:|Fatal:|Warn: IO .*not assigned)"))
            {
                throw new StudioXException("AG32_LOGIC_ROUTE", "厂商报告错误或未分配 IO，拒绝产生下载凭据。日志：" + logPath);
            }
            await Ag32GpioElectrical.VerifyAsync(run, veSource, await File.ReadAllTextAsync(Path.Combine(run, "pins.vex"), token),
                await File.ReadAllTextAsync(Path.Combine(run, "pins_routed.v"), token), token);
            var image = Path.Combine(run, "pins.bin");
            if (!File.Exists(image) || new FileInfo(image).Length is <= 0 or > 102400)
            {
                throw new StudioXException("AG32_LOGIC_IMAGE", "位流为空或超过 100 KiB 保留逻辑区。");
            }
            foreach (var (relative, expected) in hashes)
            {
                var file = PathBoundary.Resolve(root, relative);
                if ((File.Exists(file) ? await HashAsync(file, token) : "") != expected)
                {
                    throw new StudioXException("AG32_LOGIC_CHANGED", "构建期间输入已变化：" + relative);
                }
            }
            if (!await InputsCurrentAsync(root, hashes, settings, token) ||
                (await catalog.ResolveAsync(logicTools.ToolsetId, logicTools.ToolsetVersion, logicTools.CompilerId, token)).Fingerprint != native.Fingerprint ||
                (await catalog.ResolveAsync(mapping.ToolsetId, mapping.ToolsetVersion, mapping.CompilerId, token)).Fingerprint != tools.Fingerprint)
            {
                throw new StudioXException("AG32_LOGIC_CHANGED", "构建期间源码集合或工具发生变化，请重新编译。");
            }
            var artifacts = new Dictionary<string, string>();
            foreach (var name in new[] { "pins.bin", "pins.v", "pins.hx", "pins.vex", "pins.vqm", "pins_routed.v", "studiox-clocks.sdc", "studiox-gpio.asf", "setup.rpt", "hold.rpt", "fmax.rpt", "coverage.rpt" })
            {
                var file = Path.Combine(run, name);
                artifacts[Path.GetRelativePath(root, file).Replace('\\', '/')] = await HashAsync(file, token);
            }
            var imageRelative = Path.GetRelativePath(root, image).Replace('\\', '/');
            var receipt = new Ag32NativeBuildReceipt(1, settings, hashes, artifacts, tools.Fingerprint, native.Fingerprint,
                imageRelative, HdlSchematicInputs.Digest(hashes, settings.Inputs));
            await JsonStore.WriteAsync(PathBoundary.Resolve(root, Ag32NativeBuildReceipt.RelativePath), receipt, token);
            log.AppendLine($"自定义逻辑构建成功：{new FileInfo(image).Length:N0} 字节。时序诊断见 {run}");
            await File.WriteAllTextAsync(logPath, log.ToString(), token);
            return new(true, log.ToString(), artifacts.Keys.Select(file => PathBoundary.Resolve(root, file)).ToArray(), logPath, 0);
        }
        catch (Exception error)
        {
            Invalidate(root);
            log.AppendLine(error.ToString());
            await File.WriteAllTextAsync(logPath, log.ToString(), CancellationToken.None);
            if (error is OperationCanceledException)
            {
                throw;
            }
            return new(false, log.ToString(), [], logPath, 1);
        }
        finally
        {
            try
            {
                if (privateRuntime is not null)
                {
                    licensing.RemovePrivateRuntime(privateRuntime);
                }
            }
            finally { gate.Release(); }
        }

        async Task RunAsync(string executable, string[] arguments, string phase, Dictionary<string, string> environment)
        {
            progress?.Report(phase + "…");
            log.AppendLine("[" + phase + "]");
            var result = await new ProcessRunner().RunAsync(new(executable, arguments, run, TimeSpan.FromMinutes(10), environment,
                RemoveEnvironment: ToolsetEnvironment.AmbientVariables.Concat(["ALTA_HOME", "YOSYS_DATDIR", "YOSYS_ABC_EXECUTABLE"]).ToArray(), Output: output), token);
            log.AppendLine(result.StandardOutput).AppendLine(result.StandardError).AppendLine($"exit={result.ExitCode}; timeout={result.TimedOut}");
            await File.WriteAllTextAsync(logPath, log.ToString(), token);
            if (!result.Success)
            {
                throw new StudioXException("AG32_LOGIC_TOOL", phase + "失败。原始日志：" + logPath);
            }
        }
    }
}

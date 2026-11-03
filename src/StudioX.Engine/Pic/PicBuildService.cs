namespace StudioX.Engine.Pic;

using System.Security.Cryptography;
using StudioX.Foundation;

/// <summary>CMake 管理输入，Ninja 调用独立 XC8；构建不启动硬件会话。</summary>
public sealed class PicBuildService(ToolsetCatalog catalog, string dataDirectory)
{
    internal static void Invalidate(string root) => File.Delete(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath));

    internal async Task<BuildReport> ExecuteAsync(string root, ProjectManifest project, ResolvedToolset tools,
        ResolvedToolset buildTools, ProjectBuildStorage storage, bool configureOnly, string debugger, IProgress<string>? output, CancellationToken token)
    {
        PicProject.ValidateTools(tools);
        if (debugger is not ("none" or "pickit3"))
        {
            throw new StudioXException("PIC_DEBUGGER", "未知 PIC 调试构建模式。");
        }
        var source = await ProjectSourceSnapshot.HashAsync(root, true, token);
        var sourceInputs = await ProjectSourceSnapshot.HashAsync(root, false, token);
        var stage = PathBoundary.Resolve(dataDirectory, "pic/build-stage/" + Guid.NewGuid().ToString("N"));
        const string run = ".build/pic/latest";
        var destination = storage.Prepare("pic");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(destination);
        var logPath = PathBoundary.Resolve(destination, "build.log");
        try
        {
            await ProjectSourceSnapshot.CopyAsync(root, stage, token);
            if (sourceInputs != await ProjectSourceSnapshot.HashAsync(stage, false, token))
            {
                throw new StudioXException("PIC_BUILD_CHANGED", "建立构建快照时源码发生变化，请重新编译。");
            }
            var native = WindowsToolPath.Compatible(stage);
            File.Delete(PathBoundary.Resolve(stage, "firmware.hex"));
            File.Delete(PathBoundary.Resolve(stage, "firmware.elf"));
            var binary = Path.Combine(native, ".build", "cmake");
            var environment = ToolsetEnvironment.Create(tools);
            var arguments = new[] { "-S", native, "-B", binary, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + buildTools.Tool("ninja"),
                "-DPIC_XC8_COMPILER=" + WindowsToolPath.Compatible(tools.Tool("xc8-cc")), "-DPIC_DFP=" + WindowsToolPath.Compatible(tools.ResourceDirectory("pic-dfp")), "-DPIC_DEBUGGER=" + debugger,
                "-DPIC_EXPECTED_MCU=" + project.Pic!.CompilerTarget };
            output?.Report(project.DeviceId + " · CMake + Ninja + XC8；" + (debugger == "none" ? "普通构建。" : "预留 PICkit3 调试资源。"));
            var runner = new ProcessRunner();
            var result = await runner.RunAsync(new(buildTools.Tool("cmake"), arguments, native, TimeSpan.FromMinutes(3),
                environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output, ContainProcessTree: true), token);
            var log = result.StandardOutput + "\n" + result.StandardError;
            if (result.Success && !configureOnly)
            {
                result = await runner.RunAsync(new(buildTools.Tool("cmake"), ["--build", binary, "--verbose"], native, TimeSpan.FromMinutes(3),
                    environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output, ContainProcessTree: true), token);
                log += "\n" + result.StandardOutput + "\n" + result.StandardError;
                foreach (var name in new[] { "firmware.hex", "firmware.elf", "pic-inputs.json" })
                {
                    if (File.Exists(Path.Combine(binary, name)))
                    {
                        File.Copy(Path.Combine(binary, name), Path.Combine(stage, name));
                    }
                }
            }
            await ProjectSourceSnapshot.CopyAsync(stage, destination, token);
            await File.WriteAllTextAsync(logPath, log, token);
            var hex = PathBoundary.Resolve(destination, "firmware.hex");
            var elf = PathBoundary.Resolve(destination, "firmware.elf");
            var success = result.Success && (configureOnly || File.Exists(hex) && File.Exists(elf));
            if (success && !configureOnly)
            {
                if (project != await ProjectService.ReadAsync(root, token) || source != await ProjectSourceSnapshot.HashAsync(root, true, token))
                {
                    throw new StudioXException("PIC_BUILD_CHANGED", "编译期间工程或源码发生变化，请重新编译；旧 HEX 不可导出。");
                }
                await JsonStore.WriteAsync(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath), new PicBuildReceipt(2, source, tools.Fingerprint,
                    run + "/firmware.hex", await HashAsync(hex, token), run + "/firmware.elf", await HashAsync(elf, token),
                    buildTools.Fingerprint, debugger, native, await HashAsync(Path.Combine(stage, "pic-inputs.json"), token)), token);
            }
            return new(success, log, success && !configureOnly ? new[] { hex, elf, logPath } : [], logPath, result.ExitCode, result.TimedOut);
        }
        finally
        {
            // GUID 仅用于本次私有源码快照，完成后删除；工程输出使用固定 latest。
            _ = ToolsetSnapshot.Capture(stage, CancellationToken.None);
            Directory.Delete(stage, true);
        }
    }

    public async Task<PicFirmwarePreview> PreviewAsync(string root, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(root, token);
        if (project.Kind != ProjectKind.Pic)
        {
            throw new StudioXException("PROJECT_PIC", "此操作需要 PIC 工程。");
        }
        using var lease = ToolUsageLease.Acquire(PathBoundary.Resolve(catalog.RootDirectory, project.ToolsetId + "/" + project.ToolsetVersion));
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        PicProject.ValidateTools(tools);
        var needs = await ProjectDevelopmentComponents.ReadAsync(root, project, token);
        ProjectDevelopmentComponents.CheckFingerprint(await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token), tools.Manifest.Id, tools.Fingerprint);
        var path = PathBoundary.Resolve(root, PicBuildReceipt.RelativePath);
        if (!File.Exists(path))
        {
            throw new StudioXException("PIC_BUILD_REQUIRED", "请先成功编译当前 PIC 工程。");
        }
        var receipt = await JsonStore.ReadAsync<PicBuildReceipt>(path, token);
        if (receipt.FormatVersion == 2)
        {
            using var buildLease = ToolUsageLease.Acquire(PathBoundary.Resolve(catalog.RootDirectory, PicCMake.ToolsetId + "/" + PicCMake.Version));
            var buildTools = await catalog.ResolveAsync(PicCMake.ToolsetId, PicCMake.Version, PicCMake.CompilerId, token);
            ProjectDevelopmentComponents.CheckFingerprint(await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token), buildTools.Manifest.Id, buildTools.Fingerprint);
            if (receipt.BuildToolsFingerprint != buildTools.Fingerprint)
            {
                throw new StudioXException("PIC_BUILD_STALE", "PIC 的 CMake / Ninja 组件已变化，请重新编译。");
            }
        }
        var hex = PathBoundary.Resolve(root, receipt.HexRelativePath);
        var elf = PathBoundary.Resolve(root, receipt.ElfRelativePath);
        if (receipt.FormatVersion != 2 || !receipt.HexRelativePath.StartsWith(".build/pic/", StringComparison.Ordinal) ||
            !receipt.HexRelativePath.EndsWith("/firmware.hex", StringComparison.Ordinal) ||
            receipt.ElfRelativePath != Path.ChangeExtension(receipt.HexRelativePath, ".elf") ||
            receipt.SourceSha256 != await ProjectSourceSnapshot.HashAsync(root, true, token) || receipt.ToolFingerprint != tools.Fingerprint ||
            !File.Exists(hex) || !File.Exists(elf) || receipt.HexSha256 != await HashAsync(hex, token) || receipt.ElfSha256 != await HashAsync(elf, token) ||
            project != await ProjectService.ReadAsync(root, token))
        {
            throw new StudioXException("PIC_BUILD_STALE", "PIC 工程、工具或产物已变化，请重新编译后导出 HEX。");
        }
        return new(project.DeviceId, hex, receipt.HexSha256, new FileInfo(hex).Length, elf, receipt.Debugger, receipt.SourceRoot);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }

    public async Task<string[]> AnalysisFlagsAsync(string root, CancellationToken token = default)
    {
        var firmware = await PreviewAsync(root, token);
        var receipt = await JsonStore.ReadAsync<PicBuildReceipt>(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath), token);
        var path = Path.Combine(Path.GetDirectoryName(firmware.HexPath)!, "pic-inputs.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024 || receipt.AnalysisInputsSha256 != await HashAsync(path, token))
        {
            throw new StudioXException("PIC_ANALYSIS_STALE", "CMake 编辑元数据缺失或改变，请重新构建。");
        }
        var inputs = await JsonStore.ReadAsync<PicAnalysisInputs>(path, token);
        if (inputs.Includes is null || inputs.Definitions is null || inputs.Includes.Length + inputs.Definitions.Length > 4096)
        {
            throw new StudioXException("PIC_ANALYSIS_INPUTS", "CMake 编辑元数据无效。");
        }
        var stage = firmware.SourceRoot!.Replace('\\', '/').TrimEnd('/') + "/";
        return inputs.Includes.Select(include => "-I" + (include.Replace('\\', '/').StartsWith(stage, StringComparison.OrdinalIgnoreCase)
                ? PathBoundary.Resolve(root, include.Replace('\\', '/')[stage.Length..]) : include))
            .Concat(inputs.Definitions.Select(define => "-D" + define)).ToArray();
    }
}

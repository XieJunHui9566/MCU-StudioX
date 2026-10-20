namespace StudioX.Engine.Pic;

using System.Security.Cryptography;
using StudioX.Foundation;

/// <summary>直接调用独立 XC8，不启动 MPLAB IDE、IPECMD、Java 或硬件会话。</summary>
public sealed class PicBuildService(ToolsetCatalog catalog, string dataDirectory)
{
    internal static void Invalidate(string root) => File.Delete(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath));

    internal async Task<BuildReport> ExecuteAsync(string root, ProjectManifest project, ResolvedToolset tools,
        IProgress<string>? output, CancellationToken token)
    {
        PicProject.ValidateTools(tools);
        var source = await ProjectSourceSnapshot.HashAsync(root, true, token);
        var sourceInputs = await ProjectSourceSnapshot.HashAsync(root, false, token);
        var stage = PathBoundary.Resolve(dataDirectory, "pic/build-stage/" + Guid.NewGuid().ToString("N"));
        var run = ".build/pic/" + Guid.NewGuid().ToString("N");
        var destination = PathBoundary.Resolve(root, run);
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
            var inputs = ProjectSourceSnapshot.Files(stage).Where(file => file.StartsWith("src/", StringComparison.Ordinal) &&
                Path.GetExtension(file).Equals(".c", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
            if (inputs.Length == 0 || inputs.Length > 128 || ProjectSourceSnapshot.Files(stage).Any(file =>
                file.StartsWith("src/", StringComparison.Ordinal) && Path.GetExtension(file).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx" or ".s" or ".asm"))
            {
                throw new StudioXException("PIC_SOURCE", "PIC 首阶段需要 src/ 下 1～128 个 C 源文件，不接受 C++ 或独立汇编输入。");
            }
            var native = WindowsToolPath.Compatible(stage);
            File.Delete(PathBoundary.Resolve(stage, "firmware.hex"));
            File.Delete(PathBoundary.Resolve(stage, "firmware.elf"));
            var arguments = new[] { "-mcpu=" + project.Pic!.CompilerTarget, "-mdfp=" + WindowsToolPath.Compatible(tools.ResourceDirectory("pic-dfp")),
                "-std=c99", "-O0", "-Iinclude", "-Isrc", "-o", "firmware.elf" }.Concat(inputs).ToArray();
            output?.Report(project.DeviceId + " · 独立 XC8 4.00 编译；不需要 MPLAB X IDE。");
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("xc8-cc"), arguments, native, TimeSpan.FromMinutes(3),
                ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), token);
            var log = result.StandardOutput + "\n" + result.StandardError;
            await ProjectSourceSnapshot.CopyAsync(stage, destination, token);
            await File.WriteAllTextAsync(logPath, log, token);
            var hex = PathBoundary.Resolve(destination, "firmware.hex");
            var elf = PathBoundary.Resolve(destination, "firmware.elf");
            var success = result.Success && File.Exists(hex) && File.Exists(elf);
            if (success)
            {
                if (project != await ProjectService.ReadAsync(root, token) || source != await ProjectSourceSnapshot.HashAsync(root, true, token))
                {
                    throw new StudioXException("PIC_BUILD_CHANGED", "编译期间工程或源码发生变化，请重新编译；旧 HEX 不可导出。");
                }
                await JsonStore.WriteAsync(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath), new PicBuildReceipt(1, source, tools.Fingerprint,
                    run + "/firmware.hex", await HashAsync(hex, token), run + "/firmware.elf", await HashAsync(elf, token)), token);
            }
            return new(success, log, success ? new[] { hex, elf, logPath } : [], logPath, result.ExitCode, result.TimedOut);
        }
        finally
        {
            // 仅清除本次 GUID 私有目录；拒绝链接后再递归删除，不清理用户源码和历史构建。
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
        var hex = PathBoundary.Resolve(root, receipt.HexRelativePath);
        var elf = PathBoundary.Resolve(root, receipt.ElfRelativePath);
        if (receipt.FormatVersion != 1 || !receipt.HexRelativePath.StartsWith(".build/pic/", StringComparison.Ordinal) ||
            !receipt.HexRelativePath.EndsWith("/firmware.hex", StringComparison.Ordinal) ||
            receipt.ElfRelativePath != Path.ChangeExtension(receipt.HexRelativePath, ".elf") ||
            receipt.SourceSha256 != await ProjectSourceSnapshot.HashAsync(root, true, token) || receipt.ToolFingerprint != tools.Fingerprint ||
            !File.Exists(hex) || !File.Exists(elf) || receipt.HexSha256 != await HashAsync(hex, token) || receipt.ElfSha256 != await HashAsync(elf, token) ||
            project != await ProjectService.ReadAsync(root, token))
        {
            throw new StudioXException("PIC_BUILD_STALE", "PIC 工程、工具或产物已变化，请重新编译后导出 HEX。");
        }
        return new(project.DeviceId, hex, receipt.HexSha256, new FileInfo(hex).Length, elf);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }
}

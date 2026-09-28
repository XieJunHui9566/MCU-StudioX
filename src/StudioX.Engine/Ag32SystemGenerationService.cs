namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>编译 MCU 前从最新 VE 生成系统层，文本编辑与图形编辑遵守同一套时钟和命名规则。</summary>
internal sealed class Ag32SystemGenerationService(ToolsetCatalog catalog)
{
    internal async Task PrepareAsync(string root, ProjectManifest project, CancellationToken token)
    {
        if (project.PinMapping is not { } settings || Ag32DeviceCatalog.Find(project.DeviceId) is not { } profile ||
            !File.Exists(PathBoundary.Resolve(root, Ag32SystemSupport.HeaderPath))) { return; }
        var source = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, settings.PinMapFile), token);
        var tools = await catalog.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
        var run = PathBoundary.Resolve(root, ".build/ag32-system/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        await File.WriteAllBytesAsync(Path.Combine(run, "pins.ve"), source is [0xef, 0xbb, 0xbf, ..] ? source[3..] : source, token);
        var result = await new ProcessRunner().RunAsync(new(tools.Tool("python"),
            ["-I", "-B", "-X", "utf8", tools.Tool("converter"), "-d", settings.TargetDevice, "-c", "pins.hx", "pins.ve", "pins.vx", "-x", "pins.vex"],
            run, TimeSpan.FromMinutes(2), ToolsetEnvironment.Create(tools),
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables.Append("ALTA_HOME").ToArray()), token);
        await File.WriteAllTextAsync(Path.Combine(run, "converter.log"), result.StandardOutput + result.StandardError, token);
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException("AG32_SYSTEM_CONVERTER", "生成系统代码的厂商转换失败：\n" + result.StandardOutput + result.StandardError);
        }
        var functions = await Ag32PinPlanCatalog.ReadAsync(tools, settings.TargetDevice, profile.PinCount, root, token);
        var header = await File.ReadAllTextAsync(Path.Combine(run, "pins.hx"), token);
        var files = Ag32SystemSupport.Render(source, header, functions.Functions);
        await Ag32SystemSupport.WriteAsync(root, files, settings.PinMapFile, source, null, token);
    }
}

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>复用已有真实构建凭据验证预览与调试准备，不启动硬件进程。</summary>
internal static class ExistingProjectChecks
{
    public static async Task<int> RunAsync(string project, string runtime, string output)
    {
        var root = Path.GetFullPath(project);
        var resultPath = Path.GetFullPath(output);
        if (File.Exists(resultPath))
        {
            throw new ArgumentException("Use a new result file.");
        }
        var downloads = new OpenOcdService(new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets")));
        var configuration = await downloads.ConfigurationAsync(root) ?? throw new InvalidOperationException("No download configuration.");
        var preview = await downloads.PreviewAsync(root, configuration.Options);
        var debug = await HardwareDebugPreparer.PrepareAsync(root, downloads);
        if (preview.Images.Count != 2 || debug.PinMapping is null ||
            preview.Images[1].Address != 0x80027000 ||
            debug.Configuration.TargetScriptText != Ag32PinMappingTargetScript.RequireCompatible(root, configuration.OpenOcd.TargetScript))
        {
            throw new InvalidOperationException("Both verified images and embedded target guards are required.");
        }
        foreach (var probe in new[] { "cmsis-dap", "jlink" })
        {
            _ = OpenOcdDebugPlanner.Create(root, debug.Configuration with
            {
                Options = new(probe, 1000)
            }, debug.Tools, debug.Elf);
        }
        await JsonStore.WriteAsync(resultPath, new
        {
            Success = true,
            HardwareAccessed = false,
            Project = root,
            configuration.OpenOcd.TargetScript,
            preview.Images,
            preview.ApprovalSha256,
            DebugProbes = new[] { "cmsis-dap", "jlink" }
        });
        Console.WriteLine("PASS existing project: two-image preview, debug preparation and DAP/J-Link plans; no hardware access");
        return 0;
    }
}

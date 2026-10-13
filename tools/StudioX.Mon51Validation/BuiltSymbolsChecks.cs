using System.Text.Json.Nodes;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;

internal static class BuiltSymbolsChecks
{
    public static async Task<int> RunAsync(string project, string output)
    {
        var checks = new List<string>();
        var bundle = await StcDebugArtifacts.ReadAsync(project);
        var symbols = Mon51Symbols.Parse(bundle.SymbolsText, project, bundle.SourceFiles);
        if (symbols.Lines.Any(l => !File.Exists(PathBoundary.Resolve(project, Path.GetRelativePath(project, l.File).Replace('\\', '/')))))
        {
            throw new InvalidOperationException("Missing compiler source file");
        }
        checks.Add("real SDCC build receipt, image/CDB hashes and source paths");
        await using var hub = new DeviceHub();
        var fixture = new Mon51OfflineTransport { UseMemoryImage = true };
        bundle.Code.CopyTo(fixture.Code, 0);
        await using var monitor = new Mon51DebugSession(Path.Combine(output, "user-data"));
        await monitor.ConnectAsync(hub, fixture, project);
        await monitor.LoadSymbolsAsync(bundle);
        if (!monitor.HasSymbols)
        {
            throw new InvalidOperationException("Real generated CDB rejected");
        }
        checks.Add("real generated functions and source lines decode at complete instruction boundaries");
        await monitor.StopAsync();
        // 负例只改动此次隔离工程，并逐文件恢复原始字节；不修改用户工程或访问串口。
        if (!project.Contains(Path.DirectorySeparatorChar + ".artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Negative checks require isolated .artifacts project");
        }
        async Task RejectChanged(string path, byte[] changed, string code, string name)
        {
            var original = await File.ReadAllBytesAsync(path);
            try
            {
                await File.WriteAllBytesAsync(path, changed);
                try
                {
                    await StcDebugArtifacts.ReadAsync(project);
                    throw new InvalidOperationException("Accepted: " + name);
                }
                catch (StudioXException ex) when (ex.Code == code) { checks.Add(name); }
            }
            finally { await File.WriteAllBytesAsync(path, original); }
        }
        await RejectChanged(bundle.SymbolsPath, System.Text.Encoding.UTF8.GetBytes(bundle.SymbolsText + "\n"), "MON51_ARTIFACT_CHANGED", "changed CDB rejected");
        var source = symbols.Lines[0].File;
        await RejectChanged(source, System.Text.Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(source) + "\n/* changed */\n"), "MON51_SOURCE_CHANGED", "changed source rejected");
        var manifestPath = Path.Combine(project, ".studiox/project.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["deviceId"] = "STC15F2K60S2";
        await RejectChanged(manifestPath, System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()), "MON51_SYMBOLS", "changed target identity rejected");
        var settingsPath = Path.Combine(project, ProjectBuildSettings.RelativePath);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!;
        settings["debugInfo"] = "None";
        await RejectChanged(settingsPath, System.Text.Encoding.UTF8.GetBytes(settings.ToJsonString()), "MON51_SOURCE_CHANGED", "changed debug build settings rejected despite old CDB file");
        Directory.CreateDirectory(output);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            Success = true,
            Hardware = false,
            Functions = symbols.Functions.Count,
            Lines = symbols.Lines.Count,
            bundle.ImageSha256,
            bundle.SymbolsSha256,
            Checks = checks
        });
        Console.WriteLine($"PASS {checks.Count} real-build source checks");
        return 0;
    }
}

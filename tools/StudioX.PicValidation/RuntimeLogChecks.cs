namespace StudioX.PicValidation;

using StudioX.Engine;
using StudioX.Engine.Pic;
using StudioX.Foundation;

/// <summary>运行真实厂商后端的只读工具列表及 MDB 帮助，不选择或连接目标芯片。</summary>
internal static class RuntimeLogChecks
{
    internal static async Task RunAsync(string output, string runtime)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new evidence directory.");
        }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var ipe = await catalog.ResolveAsync(PicDownloadSettings.ToolsetId, "6.20.0", PicDownloadSettings.CompilerId, token);
        var mdb = await catalog.ResolveAsync(PicDebugService.ToolsetId, PicDebugService.Version, PicDebugService.CompilerId, token);
        var originalIpe = Files(ipe.RootDirectory);
        var originalMdb = Files(mdb.RootDirectory);
        var data = Path.Combine(output, "user-data");
        var service = new PicDownloadService(catalog, new BuildService(catalog).Pic, data);
        var first = await service.ListAsync(token: token);
        var second = await service.ListAsync(token: token);
        await JsonStore.WriteAsync(Path.Combine(output, "programmers.json"), new
        {
            first,
            second
        }, token);
        Check(first.Select(item => item.Serial).SequenceEqual(second.Select(item => item.Serial)), "repeated real IPECMD enumeration succeeds without target access");
        Check(File.Exists(Path.Combine(data, "pic/ipe-user/logs/MPLABXLog.xml")), "real vendor log is created beneath user data");
        Check(Files(ipe.RootDirectory).SequenceEqual(originalIpe), "enumeration adds no files to the indexed IPE component");
        await using (var process = new MdbProcess(mdb, Path.Combine(data, "mdb-user"), Path.Combine(output, "mdb.log")))
        {
            var help = await process.ExecuteAsync("help", token);
            await File.WriteAllTextAsync(Path.Combine(output, "mdb-help.txt"), help, token);
            Check(help.Length > 0 && process.IsAlive, "real MDB help runs without selecting a hardware tool");
            await process.ExecuteAsync("device PIC16F877A", token);
            await process.ExecuteAsync("hwtool SIM", token);
        }
        // MDB 会将日志重新定向到 java.io.tmpdir；两个目录都必须属于本次用户数据。
        Check(Directory.EnumerateFiles(Path.Combine(data, "mdb-user"), "MPLABXLog.xml", SearchOption.AllDirectories).Any(),
            "MDB vendor log is also created beneath user data");
        Check(Files(mdb.RootDirectory).SequenceEqual(originalMdb), "MDB startup adds no files to the indexed debug component");
        await catalog.ResolveAsync(PicDownloadSettings.ToolsetId, "6.20.0", PicDownloadSettings.CompilerId, token, forceVerification: true);
        await catalog.ResolveAsync(PicDebugService.ToolsetId, PicDebugService.Version, PicDebugService.CompilerId, token, forceVerification: true);
        Check(true, "both toolsets pass full content verification after their real backends exit");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            checks,
            targetConnected = false,
            firmwareWritten = false
        }, token);
    }

    private static string[] Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
}

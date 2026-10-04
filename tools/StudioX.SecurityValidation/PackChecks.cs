namespace StudioX.SecurityValidation;

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>使用隔离目录连接验证包仓库边界，不访问真实安装目录。</summary>
internal static class PackChecks
{
    internal static async Task RunAsync(string root)
    {
        var source = Path.Combine(root, "pack-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "main.c"), "int main(void) { return 0; }\n");
        await File.WriteAllTextAsync(Path.Combine(source, "memory.ld"), "SECTIONS {}\n");
        var device = new DeviceDefinition("fixture", "Fixture", "arm", 0x08000000, 1024, 0x20000000, 1024,
            "fixture.tools", "1.0.0", "fixture-compiler", [], [], [], ["main.c"], "memory.ld", [], [],
            [new ProjectTemplate("minimal", "Minimal", "Offline fixture", "main.c")]);
        await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"),
            new PackManifest(1, "fixture.pack", "1.0.0", "Fixture", "Offline", [device]));
        var archive = Path.Combine(root, "fixture.mcupack");
        await PackArchiveWriter.WriteAsync(source, archive);
        var repository = new PackRepository(Path.Combine(root, "packs"));
        var installed = await repository.ImportAsync(archive);
        Require((await repository.ListCatalogAsync()).Count == 1, "valid pack remains selectable");
        Require((await PackRepository.VerifyAsync(installed)).ContentHash == installed.ContentHash, "valid pack verifies");
        Require((await repository.ImportAsync(archive)).ContentHash == installed.ContentHash, "valid pack import is idempotent");

        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var linked = Path.Combine(root, "linked-root");
        await JunctionAsync(root, linked, outside);
        try
        {
            await RejectAsync(() => new PackRepository(linked).ImportAsync(archive), "linked repository root");
            await RejectAsync(() => new PackRepository(Path.Combine(linked, "nested")).ImportAsync(archive), "linked repository ancestor");
            Require(!Directory.EnumerateFileSystemEntries(outside).Any(), "rejected import writes no outside files");
        }
        finally
        {
            Directory.Delete(linked);
        }

        var version = Path.GetDirectoryName(installed.RootDirectory)!;
        var moved = Path.Combine(root, "outside-version");
        Directory.Move(version, moved);
        await JunctionAsync(root, version, moved);
        try
        {
            await RejectAsync(() => repository.ListCatalogAsync(), "linked installed version catalog");
            await RejectAsync(() => repository.ListAsync(), "linked installed version full list");
            await RejectAsync(() => PackRepository.VerifyAsync(installed), "linked selected version verification");
        }
        finally
        {
            Directory.Delete(version);
            Directory.Move(moved, version);
        }

        var extra = Path.Combine(installed.RootDirectory, "unindexed-link");
        await JunctionAsync(root, extra, outside);
        try
        {
            await RejectAsync(() => PackRepository.VerifyAsync(installed), "unindexed empty directory link");
        }
        finally
        {
            Directory.Delete(extra);
        }

        var bad = Path.Combine(root, "traversal.mcupack");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("../escaped.txt").Open())
            {
                entry.Write(Encoding.UTF8.GetBytes("escape"));
            }
            zip.CreateEntry("files.sha256.json");
        }
        await RejectAsync(() => repository.ImportAsync(bad), "archive traversal", "PATH_UNSAFE");
        Require(!File.Exists(Path.Combine(repository.RootDirectory, "escaped.txt")), "archive traversal creates no escape file");

        var sourceLink = Path.Combine(source, "unindexed-link");
        await JunctionAsync(root, sourceLink, outside);
        try
        {
            await RejectAsync(() => PackArchiveWriter.WriteAsync(source, Path.Combine(root, "linked-source.mcupack")), "linked pack source tree");
        }
        finally
        {
            Directory.Delete(sourceLink);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await repository.ImportAsync(archive, cancelled.Token);
            throw new InvalidOperationException("Cancelled pack import was accepted.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
        {
            Require(!Directory.GetDirectories(repository.RootDirectory).Any(path => Path.GetFileName(path).StartsWith(".import-")),
                "cancelled pack import leaves no staging");
        }
    }

    private static async Task JunctionAsync(string root, string link, string target)
    {
        var script = Path.Combine(root, "junction.ps1");
        await File.WriteAllTextAsync(script,
            "param([string]$Link,[string]$Target)\nNew-Item -Path $Link -ItemType Junction -Value $Target -ErrorAction Stop | Out-Null\n");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Link", link, "-Target", target })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await output + await error);
        }
    }

    private static async Task RejectAsync(Func<Task> action, string description, string code = "PATH_LINK")
    {
        try
        {
            await action();
        }
        catch (StudioXException error) when (error.Code == code)
        {
            Console.WriteLine("PASS " + description);
            return;
        }
        throw new InvalidOperationException("Expected " + code + ": " + description);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine("PASS " + description);
    }
}

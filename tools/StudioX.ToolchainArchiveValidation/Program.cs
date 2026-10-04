using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.ToolchainArchiveValidation;

if (args.Length == 4 && args[0] is "--real" or "--metadata")
{
    await RealArchiveChecks.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), args[0] == "--real");
    return;
}
if (args is ["--build", var buildOutput, var toolsRoot, var pack, var device, var template])
{
    await RealArchiveChecks.BuildAsync(Path.GetFullPath(buildOutput), Path.GetFullPath(toolsRoot), Path.GetFullPath(pack), device, template);
    return;
}
if (args.Length != 3) { throw new ArgumentException("Use <new-output> <repository> <7z.exe>."); }
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) { throw new ArgumentException("Use new output."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool condition, string text) { if (!condition) { throw new InvalidOperationException(text); } checks.Add(text); Console.WriteLine("PASS " + text); }
async Task Reject(Func<Task> action, string code)
{
    try
    {
        await action();
        throw new InvalidOperationException("Expected " + code);
    }
    catch (StudioXException error) when (error.Code == code) { Check(true, "reject " + code); }
}
async Task Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var log = await stdout + await stderr;
    await File.AppendAllTextAsync(Path.Combine(output, "native-7z.log"), log);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(log);
    }
}
try
{
    var root = Path.Combine(output, "source", "test.archive", "1.0.0");
    Directory.CreateDirectory(root);
    var payload = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var name in Enumerable.Range(0, 24).Select(i => "include/file-" + i + ".h").Append("中文 名称.txt").Append("empty.txt").Append("bin/probe.exe"))
    {
        var path = PathBoundary.Resolve(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = name == "empty.txt" ? [] : name == "bin/probe.exe" ? await File.ReadAllBytesAsync(Path.Combine(args[1], "artifacts/tool-runtime/toolsets/arm.gnu/1.0.0/ninja/ninja.exe")) :
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("/* shared SDK declarations */\nint function(int argument);\n", 512)) + name);
        await File.WriteAllBytesAsync(path, content);
        payload.Add(name, Convert.ToHexString(SHA256.HashData(content)));
    }
    var manifest = new ToolsetManifest(1, "test.archive", "1.0.0", "win-x64", "test-archive",
        new[] { "gcc", "gxx", "objcopy", "size", "cmake", "ninja" }.ToDictionary(role => role, _ => "bin/probe.exe"), payload, "7z fixture");
    await JsonStore.WriteAsync(Path.Combine(root, "toolset.json"), manifest);
    // 原始 BOM、空白和尾部换行也必须完整保留，不能通过再序列化制造新内容锁。
    var json = await File.ReadAllBytesAsync(Path.Combine(root, "toolset.json"));
    var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(json).Concat(new byte[] { 13, 10 }).ToArray();
    await File.WriteAllBytesAsync(Path.Combine(root, "toolset.json"), original);
    var catalog = new ToolsetCatalog(Path.Combine(output, "source"));
    var entry = new ToolEnvironmentEntry(manifest.Id, manifest.Version, "fixture", manifest.CompilerId, 0, 0, false, "");
    var exported = Path.Combine(output, "managed.mcutoolchain");
    await new ToolEnvironmentService(catalog).ExportAsync(entry, exported, null);
    await Run(args[2], "t", "-bd", exported);
    using (var stream = File.OpenRead(exported))
    {
        using (var container = ToolchainArchive.Open(stream))
        {
            Check(container.Container == "7z" && (await container.ReadManifestAsync()).SequenceEqual(original), "managed export is native-readable 7z and preserves BOM/whitespace manifest bytes");
        }
    }
    var solid = Path.Combine(output, "solid.mcutoolchain");
    await Run("pwsh", "-NoProfile", "-File", Path.Combine(Path.GetFullPath(args[1]), "tools/New-McuToolchain.ps1"),
        "-ToolsetDirectory", root, "-OutputFile", solid, "-SevenZipPath", Path.GetFullPath(args[2]));
    using (var stream = File.OpenRead(solid))
    {
        using (var container = ToolchainArchive.Open(stream))
        {
            Check(container.Container == "7z" && container.Entries.Count == payload.Count + 1, "native release packing has exact file set and no directory entries");
            Check((await container.ReadManifestAsync()).SequenceEqual(original), "solid release preserves original manifest bytes");
        }
    }
    var installed = new ToolsetCatalog(Path.Combine(output, "installed tools"), Path.Combine(output, "user-data"));
    var manager = new ToolManagementService(installed, new(Path.Combine(output, "packs")), new(output), output);
    var preview = await manager.PreviewInstallAsync(solid);
    Check(!(await manager.InstallAsync(preview)).AlreadyInstalled, "solid native 7z installs using sequential extraction");
    Check((await manager.InstallAsync(await manager.PreviewInstallAsync(exported))).AlreadyInstalled, "same component is idempotent across solid/native and non-solid/managed containers");
    var installedRoot = Path.Combine(installed.RootDirectory, manifest.Id, manifest.Version);
    Check((await File.ReadAllBytesAsync(Path.Combine(installedRoot, "toolset.json"))).SequenceEqual(original) && new FileInfo(Path.Combine(installedRoot, "empty.txt")).Length == 0,
        "solid extraction preserves empty file and original manifest bytes");
    await File.WriteAllTextAsync(Path.Combine(installedRoot, "中文 名称.txt"), "damaged");
    var rollback = await new ToolEnvironmentService(installed).RepairAsync(entry, solid, null);
    Check(Directory.Exists(rollback) && (await manager.InstallAsync(await manager.PreviewInstallAsync(exported))).AlreadyInstalled, "7z repair verifies full copy and retains original rollback");
    var planService = new ProjectToolPreparationService(installed, manager);
    Check((await planService.PreviewCompatibilityAsync(null, await manager.PreviewInstallAsync(solid))).CanInstall, "project preparation accepts real 7z manifest");

    async Task Bad(string name, string expected, params MinimalSevenZip.File[] files)
    {
        var path = Path.Combine(output, name + ".mcutoolchain");
        MinimalSevenZip.Write(path, files);
        await Reject(() => manager.PreviewInstallAsync(path), expected);
    }
    var description = new MinimalSevenZip.File("toolset.json", original);
    await Bad("traversal", "PATH_UNSAFE", description, new("../escape", [1]));
    await Bad("drive", "PATH_UNSAFE", description, new("C:/escape", [1]));
    await Bad("rooted", "PATH_UNSAFE", description, new("/escape", [1]));
    await Bad("backslash", "PATH_UNSAFE", description, new("bin\\escape", [1]));
    await Bad("reserved", "PATH_UNSAFE", description, new("CON.txt", [1]));
    await Bad("case-duplicate", "TOOLS_ARCHIVE_ENTRY", description, new("file", [1]), new("FILE", [2]));
    await Bad("parent-conflict", "TOOLS_ARCHIVE_ENTRY", description, new("bin", [1]), new("bin/probe.exe", [2]));
    await Bad("directory", "TOOLS_ARCHIVE_ENTRY", description, new("directory/", [1]));
    await Bad("reparse", "TOOLS_ARCHIVE_LINK", description, new("link", [1], Attributes: (int)FileAttributes.ReparsePoint));
    await Bad("unix-link", "TOOLS_ARCHIVE_LINK", description, new("link", [1], Attributes: unchecked((int)0xa1ff8000)));
    await Bad("anti-entry", "TOOLS_ARCHIVE_ENTRY", description, new("deleted", [], Anti: true));
    await Bad("encrypted", "TOOLS_ARCHIVE_ENCRYPTED", description, new("encrypted", [1], Encrypted: true));
    await Bad("unindexed", "TOOLS_ARCHIVE_ENTRY", description, new("extra.txt", [1]));
    await Bad("unpacked-limit", "TOOLS_ARCHIVE_SIZE", description, new("large", [1], Length: ToolchainArchive.MaximumBytes + 1));
    await Bad("dictionary-limit", "TOOLS_ARCHIVE_SIZE", description, new("dictionary", [1], Lzma2Properties: [40]));
    var countBomb = Path.Combine(output, "file-count-limit.mcutoolchain");
    MinimalSevenZip.Write(countBomb, [description, new("file", [1])], 1_000_000_000);
    await Reject(() => manager.PreviewInstallAsync(countBomb), "TOOLS_ARCHIVE_SIZE");
    var headerBomb = Path.Combine(output, "decoded-header-limit.mcutoolchain");
    MinimalSevenZip.EncodedHeaderBomb(headerBomb);
    await Reject(() => manager.PreviewInstallAsync(headerBomb), "TOOLS_ARCHIVE_SIZE");
    var fake = Path.Combine(output, "fake.mcutoolchain");
    await File.WriteAllTextAsync(fake, "this is not an archive");
    await Reject(() => manager.PreviewInstallAsync(fake), "TOOLS_ARCHIVE_FORMAT");
    var truncated = Path.Combine(output, "truncated.mcutoolchain");
    File.Copy(solid, truncated);
    using (var stream = File.OpenWrite(truncated))
    {
        stream.SetLength(stream.Length / 2);
    }
    await Reject(() => manager.PreviewInstallAsync(truncated), "TOOLS_ARCHIVE_SIZE");

    var corruptRoot = Path.Combine(output, "corrupt-source");
    Directory.CreateDirectory(corruptRoot);
    await File.WriteAllBytesAsync(Path.Combine(corruptRoot, "toolset.json"), original);
    foreach (var name in payload.Keys)
    {
        var path = PathBoundary.Resolve(corruptRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(PathBoundary.Resolve(root, name), path);
    }
    await File.WriteAllTextAsync(Path.Combine(corruptRoot, "中文 名称.txt"), "corrupt source");
    var corrupt = Path.Combine(output, "corrupt.mcutoolchain");
    await ToolchainArchiveWriter.WriteAsync(corruptRoot, payload.Keys.Prepend("toolset.json"), corrupt);
    await Reject(async () => { await manager.InstallAsync(await manager.PreviewInstallAsync(corrupt)); }, "TOOL_HASH");
    var before = await File.ReadAllBytesAsync(Path.Combine(installedRoot, "中文 名称.txt"));
    await Reject(() => new ToolEnvironmentService(installed).RepairAsync(entry, corrupt, null), "TOOL_HASH");
    Check((await File.ReadAllBytesAsync(Path.Combine(installedRoot, "中文 名称.txt"))).SequenceEqual(before) && !Directory.EnumerateDirectories(installed.RootDirectory, ".repair-*").Any(),
        "invalid 7z payload leaves installed content intact and staging cleaned");
    using (var cancelled = new CancellationTokenSource())
    {
        var cancelOutput = Path.Combine(output, "cancelled.mcutoolchain");
        var progress = new ImmediateProgress(_ => cancelled.Cancel());
        try
        {
            await new ToolEnvironmentService(catalog).ExportAsync(entry, cancelOutput, progress, cancelled.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException) { Check(!File.Exists(cancelOutput) && !Directory.EnumerateFiles(output, "cancelled.mcutoolchain.tmp-*").Any(), "cancelled managed export removes partial archive"); }
    }
    using (var cancelled = new CancellationTokenSource())
    {
        var cancelRoot = Path.Combine(output, "cancel-install");
        try
        {
            await new ToolEnvironmentService(new(cancelRoot)).RepairAsync(entry, solid,
                new ImmediateProgress(text => { if (text.StartsWith("校验副本：", StringComparison.Ordinal)) { cancelled.Cancel(); } }), cancelled.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            Check(!Directory.Exists(Path.Combine(cancelRoot, entry.Id, entry.Version)) && !Directory.EnumerateDirectories(cancelRoot, ".repair-*").Any(),
                "cancelled solid import leaves no installed component or partial staging");
        }
    }
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        success = true,
        hardware = false,
        checks,
        managedBytes = new FileInfo(exported).Length,
        solidBytes = new FileInfo(solid).Length
    });
}
catch (Exception error)
{
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        success = false,
        hardware = false,
        checks,
        diagnostic = error.ToString()
    });
    throw;
}

sealed class ImmediateProgress(Action<string> callback) : IProgress<string>
{
    public void Report(string value) => callback(value);
}

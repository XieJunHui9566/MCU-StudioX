using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--hold", var leasedRoot])
{
    using var lease = ToolUsageLease.Acquire(leasedRoot);
    Console.WriteLine("READY");
    Console.Out.Flush();
    Console.ReadLine();
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: tool-management-validation <new-output> <ninja.exe>");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new InvalidOperationException("Use a new evidence directory.");
Directory.CreateDirectory(output);
var ninja = await File.ReadAllBytesAsync(args[1]);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> operation, string code)
{
    try { await operation(); throw new InvalidOperationException("Expected " + code); }
    catch (StudioXException error) when (error.Code == code) { Check(true, "reject " + code); }
}
async Task<ToolsetCatalog> MakeTools(string parent, params string[] versions)
{
    var root = Path.Combine(parent, "runtime", "toolsets");
    foreach (var version in versions)
    {
        var folder = Path.Combine(root, "test.gcc", version);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "probe.exe"), ninja);
        await JsonStore.WriteAsync(Path.Combine(folder, "toolset.json"), new ToolsetManifest(1, "test.gcc", version, "win-x64", "test-gcc",
            new[] { "gcc", "gxx", "objcopy", "size", "cmake", "ninja" }.ToDictionary(role => role, _ => "probe.exe"),
            new() { ["probe.exe"] = Convert.ToHexString(SHA256.HashData(ninja)) }, "测试工具", new() { ["fixture"] = version }));
    }
    return new(root);
}
async Task<string> MakeProject(string name, string version)
{
    var root = Path.Combine(output, "中文 工程", name);
    await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"),
        new ProjectManifest(1, name, "test.pack", "1.0.0", "fixture", "TestDevice", "minimal", "test.gcc", version, "test-gcc"));
    await File.WriteAllTextAsync(Path.Combine(root, "sdkconfig"), "keep config");
    return root;
}
var catalog = await MakeTools(Path.Combine(output, "本地 工具"), "1.0.0", "1.1.0", "2.0.0");
var data = Path.Combine(output, "user-data");
var recent = new RecentProjectService(data);
var packs = new PackRepository(Path.Combine(data, "packs"));
var service = new ToolManagementService(catalog, packs, recent, data);
var report = await service.InspectAsync(null);
Check(report.ReferencesComplete && report.Versions.Count == 3, "inspection lists side-by-side versions in Chinese and spaced paths");
Check(report.Versions.Single(version => version.Version == "2.0.0").Latest, "newest installed version is reserved");
Check(!report.Versions.Single(version => version.Version == "2.0.0").CanRetire, "latest version cannot be retired");
Check(report.Versions.Single(version => version.Version == "1.1.0").CanRetire, "unreferenced older version offers a reversible removal");
Check(report.InstalledBytes == Directory.EnumerateFiles(catalog.RootDirectory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length), "space count includes manifests and all actual files");
Check(report.Summary.Contains("逻辑文件大小"), "shared hard links are not advertised as guaranteed reclaimed space");
var project = await MakeProject("locked", "1.0.0");
var projectBytes = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
var old = report.Versions.Single(version => version.Version == "1.0.0");
await Reject(() => service.RetireAsync(old, project), "TOOLS_PROTECTED");
await recent.RememberAsync("locked", project);
Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.0.0").References.Any(reference => reference.Contains(project)), "recent project protects its exact version");
var extra = await MakeProject("registered", "1.1.0");
await service.RegisterProjectAsync(extra);
Check(!(await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0").CanRetire, "registered closed project protects its exact version");
await service.ForgetProjectAsync(extra);
Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0").CanRetire, "explicitly removed registration updates the known scope");
await JsonStore.WriteAsync(Path.Combine(project, ".studiox", "toolchain.lock.json"), new ToolchainLock(1, "test.gcc", "1.1.0", "extra lock"));
Check(!(await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0").CanRetire, "independent content lock remains protected even when manifest differs");
File.Move(Path.Combine(project, ".studiox", "toolchain.lock.json"), Path.Combine(output, "preserved-extra-lock.json"));
var packSource = Path.Combine(output, "pack-source");
Directory.CreateDirectory(packSource);
await File.WriteAllTextAsync(Path.Combine(packSource, "main.c"), "int main(void) { return 0; }");
await File.WriteAllTextAsync(Path.Combine(packSource, "link.ld"), "/* fixture */");
var device = new DeviceDefinition("TestDevice", "TestDevice", "arm", 0x08000000, 65536, 0x20000000, 16384, "test.gcc", "2.0.0", "test-gcc",
    ["-mcpu=cortex-m4", "-mthumb"], [], [], ["main.c"], "link.ld", [], [], [new("minimal", "Minimal", "Fixture", "main.c")]);
await JsonStore.WriteAsync(Path.Combine(packSource, "manifest.json"), new PackManifest(1, "test.pack", "1.0.0", "Fixture", "Fixture", [device]));
var packArchive = Path.Combine(output, "fixture.mcupack");
await PackArchiveWriter.WriteAsync(packSource, packArchive);
await packs.ImportAsync(packArchive);
Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "2.0.0").References.Any(reference => reference.Contains("器件包")), "installed device pack reserves its declared compiler version");
var packIndex = Path.Combine(packs.RootDirectory, "test.pack", "1.0.0", "files.sha256.json");
var packIndexBytes = await File.ReadAllBytesAsync(packIndex);
await File.WriteAllTextAsync(packIndex, "invalid index");
Check((await service.InspectAsync(null)).Versions.All(version => !version.CanRetire), "damaged device pack dependency prevents cleanup of every candidate");
await File.WriteAllBytesAsync(packIndex, packIndexBytes);
await recent.RememberAsync("offline", Path.Combine(output, "unavailable-drive-project"));
var incomplete = await service.InspectAsync(null);
Check(!incomplete.ReferencesComplete && incomplete.Versions.All(version => !version.CanRetire), "unreadable recent project blocks cleanup instead of assuming unused");
Check(incomplete.Diagnostics.Any(diagnostic => diagnostic.Contains("Exception")), "unreadable dependency retains full original diagnostic");
await recent.RemoveAsync(Path.Combine(output, "unavailable-drive-project"));
var oldRoot = Path.Combine(catalog.RootDirectory, "test.gcc", "1.1.0");
using (var lease = ToolUsageLease.Acquire(oldRoot))
{
    Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0").Busy, "active shared lease is visible in occupancy report");
    await Reject(() => service.RetireAsync(report.Versions.Single(version => version.Version == "1.1.0"), null), "TOOLS_PROTECTED");
    await Reject(() => Task.Run(() => { using var exclusive = ToolUsageLease.Acquire(oldRoot, true); }), "TOOLS_BUSY");
}
using (var exclusive = ToolUsageLease.Acquire(oldRoot, true))
    await Reject(() => catalog.ResolveAsync("test.gcc", "1.1.0", "test-gcc"), "TOOLS_BUSY");
using (var executableLease = ToolUsageLease.ForExecutable(Path.Combine(oldRoot, "probe.exe")))
    Check(executableLease is not null && ToolUsageLease.IsBusy(oldRoot), "process executable automatically resolves its toolset lease");
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
start.ArgumentList.Add("--hold"); start.ArgumentList.Add(oldRoot);
using (var child = Process.Start(start)!)
{
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "separate process holds a live tool lease");
    await Reject(() => Task.Run(() => { using var lease = ToolUsageLease.Acquire(oldRoot, true); }), "TOOLS_BUSY");
    await child.StandardInput.WriteLineAsync("finish");
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(child.ExitCode == 0 && !ToolUsageLease.IsBusy(oldRoot), "cross-process occupancy releases after process exit");
}
var fresh = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0");
var guarded = new ToolManagementService(catalog, packs, recent, data, () => true);
await Reject(() => guarded.RetireAsync(fresh, null), "TOOLS_SESSION_ACTIVE");
var stale = fresh;
await File.WriteAllTextAsync(Path.Combine(oldRoot, "extra.tmp"), "external change");
await Reject(() => service.RetireAsync(stale, null), "TOOLS_CHANGED");
File.Move(Path.Combine(oldRoot, "extra.tmp"), Path.Combine(output, "preserved-extra.tmp"));
fresh = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0");
var backup = await service.RetireAsync(fresh, null);
Check(!Directory.Exists(oldRoot) && Directory.Exists(backup), "retirement moves only the chosen version into recoverable storage");
var retired = (await service.InspectAsync(null)).Versions.Single(version => !version.Installed);
Check(retired.Bytes == fresh.Bytes && (await service.InspectAsync(null)).RetiredBytes == retired.Bytes, "retired bytes remain explicitly counted as disk use");
await service.RestoreAsync(retired);
Check(Directory.Exists(oldRoot) && (await service.InspectAsync(null)).Versions.All(version => version.Installed), "full verification restores the exact old version");
var newSource = await MakeTools(Path.Combine(output, "upgrade-source"), "10.0.0");
var newEntry = new ToolEnvironmentEntry("test.gcc", "10.0.0", "new", "test-gcc", 0, 0, false, "");
var archive = Path.Combine(output, "upgrade.studioxtools");
await new ToolEnvironmentService(newSource).ExportAsync(newEntry, archive, null);
var preview = await service.PreviewInstallAsync(archive);
Check(preview.Id == "test.gcc" && preview.Version == "10.0.0" && preview.InstalledVersions.Count == 3, "upgrade preview shows exact identity and existing versions");
Check(!Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0")), "preview alone does not install");
await service.InstallAsync(preview);
Check((await service.InspectAsync(null)).Versions.Count == 4, "new version is installed side by side");
Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "10.0.0").Latest, "numeric version 10 sorts after 2");
var projectAfterUpgrade = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
Check(projectBytes.SequenceEqual(projectAfterUpgrade), "upgrade never changes existing project manifest");
Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(catalog.RootDirectory, "test.gcc", "1.0.0", "probe.exe")))) == Convert.ToHexString(SHA256.HashData(ninja)), "upgrade preserves old compiler contents");
await Reject(() => service.InstallAsync(preview), "TOOLS_VERSION_EXISTS");
var nextSource = await MakeTools(Path.Combine(output, "changed-source"), "11.0.0");
var changedArchive = Path.Combine(output, "changed.studioxtools");
await new ToolEnvironmentService(nextSource).ExportAsync(newEntry with { Version = "11.0.0" }, changedArchive, null);
var changedPreview = await service.PreviewInstallAsync(changedArchive);
using (var zip = ZipFile.Open(changedArchive, ZipArchiveMode.Update)) { using var text = new StreamWriter(zip.GetEntry("probe.exe")!.Open()); text.Write("corrupt"); }
await Reject(() => service.InstallAsync(changedPreview), "TOOLS_CHANGED");
var corruptPreview = await service.PreviewInstallAsync(changedArchive);
await Reject(() => service.InstallAsync(corruptPreview), "TOOL_HASH");
Check(!Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "11.0.0")), "invalid content is never published as installed");
var traversalArchive = Path.Combine(output, "traversal.studioxtools");
File.Copy(changedArchive, traversalArchive);
using (var zip = ZipFile.Open(traversalArchive, ZipArchiveMode.Update)) { using var text = new StreamWriter(zip.CreateEntry("../escape.exe").Open()); text.Write("forbidden"); }
await Reject(() => service.PreviewInstallAsync(traversalArchive), "PATH_UNSAFE");
Check(!File.Exists(Path.Combine(output, "escape.exe")), "archive traversal never writes outside staging");
var toPurge = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "1.1.0");
var toPurgeDirectory = await service.RetireAsync(toPurge, null);
var purgeEntry = (await service.InspectAsync(null)).Versions.Single(version => !version.Installed);
await service.RegisterProjectAsync(extra);
await Reject(() => service.PurgeAsync(purgeEntry, null), "TOOLS_PROTECTED");
await service.ForgetProjectAsync(extra);
var retiredProbe = Path.Combine(toPurgeDirectory, "test.gcc", "1.1.0", "probe.exe");
var sharedProbe = Path.Combine(output, "shared-probe.exe");
Check(NativeLinks.CreateHardLink(sharedProbe, retiredProbe, IntPtr.Zero), "fixture shares a real NTFS hard link outside the retired directory");
using (var occupied = new FileStream(retiredProbe, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { await service.PurgeAsync(purgeEntry, null); throw new InvalidOperationException("Expected occupied file failure"); }
    catch (IOException) { Check(File.Exists(Path.Combine(toPurgeDirectory, "retirement.json")), "interrupted permanent deletion retains its management record"); }
}
purgeEntry = (await service.InspectAsync(null)).Versions.Single(version => !version.Installed);
Check(!purgeEntry.CanRestore && purgeEntry.CanPurge && purgeEntry.RetirementState == "purging", "partially deleted version offers retry without pretending it can restore");
await Reject(() => service.RestoreAsync(purgeEntry), "TOOLS_RETIREMENT");
await service.PurgeAsync(purgeEntry, null);
Check(!Directory.Exists(toPurgeDirectory) && Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "1.0.0")), "permanent cleanup removes only the unreferenced retired version");
Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sharedProbe))) == Convert.ToHexString(SHA256.HashData(ninja)), "purging one hard link preserves other shared copies");
Check(await File.ReadAllTextAsync(Path.Combine(project, "sdkconfig")) == "keep config", "cleanup preserves project SDK configuration");
var final = await service.InspectAsync(null);
await service.ExportReportAsync(final, Path.Combine(output, "usage.json"));
Check((await JsonStore.ReadAsync<ToolManagementReport>(Path.Combine(output, "usage.json"))).Projects.Contains(project), "export records the actual dependency scope");
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {checks.Count} checks.");

internal static class NativeLinks
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newName, string existing, IntPtr security);
}

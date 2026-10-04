using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
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
if (args.Length != 2) { throw new ArgumentException("Usage: tool-management-validation <new-output> <ninja.exe>"); }
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) { throw new InvalidOperationException("Use a new evidence directory."); }
Directory.CreateDirectory(output);
var ninja = await File.ReadAllBytesAsync(args[1]);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) { throw new InvalidOperationException(label); } checks.Add(label); Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> operation, string code)
{
    try
    {
        await operation();
        throw new InvalidOperationException("Expected " + code);
    }
    catch (StudioXException error) when (error.Code == code) { Check(true, "reject " + code); }
}
async Task LegacyZipFixture(string source, string destination)
{
    var temporary = destination + ".zip-fixture";
    using (var file = File.OpenRead(source))
    {
        using (var container = ToolchainArchive.Open(file))
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                await container.ReadFilesAsync(async (entry, content) =>
            {
                using var output = zip.CreateEntry(entry.Name).Open();
                await ToolchainArchive.CopyExactAsync(content, output, entry.Length);
            });
            }
        }
    }
    File.Move(temporary, destination, true);
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
            new()
            {
                ["probe.exe"] = Convert.ToHexString(SHA256.HashData(ninja))
            }, "测试工具", new()
            {
                ["fixture"] = version
            }));
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
catalog = new(catalog.RootDirectory, data);
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
{
    await Reject(() => catalog.ResolveAsync("test.gcc", "1.1.0", "test-gcc"), "TOOLS_BUSY");
}
using (var executableLease = ToolUsageLease.ForExecutable(Path.Combine(oldRoot, "probe.exe")))
{
    Check(executableLease is not null && ToolUsageLease.IsBusy(oldRoot), "process executable automatically resolves its toolset lease");
}
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
start.ArgumentList.Add("--hold");
start.ArgumentList.Add(oldRoot);
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
var preinstalledRoot = Path.Combine(catalog.RootDirectory, "test.gcc", "1.0.0");
var preinstalledManifest = await File.ReadAllBytesAsync(Path.Combine(preinstalledRoot, "toolset.json"));
var preinstalledFingerprint = Convert.ToHexString(SHA256.HashData(preinstalledManifest)).ToLowerInvariant();
var lockFile = Path.Combine(project, ".studiox", "toolchain.lock.json");
await JsonStore.WriteAsync(lockFile, new ToolchainLock(1, "test.gcc", "1.0.0", preinstalledFingerprint));
var lockBytes = await File.ReadAllBytesAsync(lockFile);
var originalPath = Environment.GetEnvironmentVariable("PATH");
var preinstalledArchive = Path.Combine(output, "preinstalled.mcutoolchain");
await new ToolEnvironmentService(catalog).ExportAsync(newEntry with { Version = "1.0.0" }, preinstalledArchive, null);
var preinstalledPreview = await service.PreviewInstallAsync(preinstalledArchive);
Check(preinstalledPreview.AlreadyInstalled && preinstalledPreview.Identity.Key == "test.gcc/1.0.0/win-x64"
    && preinstalledPreview.Fingerprint == preinstalledFingerprint, "preinstalled and imported components share stable identity and original content fingerprint");
using (var file = File.OpenRead(preinstalledArchive))
{
    using (var container = ToolchainArchive.Open(file))
    {
        Check(container.Container == "7z", "default IDE export uses real 7z container");
        Check((await container.ReadManifestAsync()).SequenceEqual(preinstalledManifest), "mcutoolchain export preserves original manifest bytes without changing project locks");
    }
}
var originalStamp = File.GetLastWriteTimeUtc(Path.Combine(preinstalledRoot, "probe.exe"));
Check((await service.InstallAsync(preinstalledPreview)).AlreadyInstalled
    && File.GetLastWriteTimeUtc(Path.Combine(preinstalledRoot, "probe.exe")) == originalStamp, "importing identical bundled component verifies and skips all installed file writes");
var renamedArchive = Path.Combine(output, "unrelated-file-name.MCUTOOLCHAIN");
File.Copy(preinstalledArchive, renamedArchive);
Check((await service.InstallAsync(await service.PreviewInstallAsync(renamedArchive))).Identity == preinstalledPreview.Identity,
    "component identity comes from manifest rather than archive filename or extension case");
var legacyArchive = Path.Combine(output, "preinstalled.studioxtools");
await LegacyZipFixture(preinstalledArchive, legacyArchive);
Check((await service.InstallAsync(await service.PreviewInstallAsync(legacyArchive))).AlreadyInstalled, "existing studioxtools archives remain explicit format-1 import aliases");
var archive = Path.Combine(output, "upgrade.mcutoolchain");
await new ToolEnvironmentService(newSource).ExportAsync(newEntry, archive, null);
var preview = await service.PreviewInstallAsync(archive);
Check(preview.Id == "test.gcc" && preview.Version == "10.0.0" && preview.InstalledVersions.Count == 3, "upgrade preview shows exact identity and existing versions");
Check(!Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0")), "preview alone does not install");
Check(!(await service.InstallAsync(preview)).AlreadyInstalled, "new development component reports a distinct side-by-side installation");
Check((await service.InspectAsync(null)).Versions.Count == 4, "new version is installed side by side");
Check((await service.InspectAsync(null)).Versions.Single(version => version.Version == "10.0.0").Latest, "numeric version 10 sorts after 2");
var projectAfterUpgrade = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
Check(projectBytes.SequenceEqual(projectAfterUpgrade), "upgrade never changes existing project manifest");
Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(catalog.RootDirectory, "test.gcc", "1.0.0", "probe.exe")))) == Convert.ToHexString(SHA256.HashData(ninja)), "upgrade preserves old compiler contents");
Check((await service.InstallAsync(preview)).AlreadyInstalled, "repeated installation of unchanged component is idempotent");
Check((await File.ReadAllBytesAsync(lockFile)).SequenceEqual(lockBytes) && Environment.GetEnvironmentVariable("PATH") == originalPath,
    "new and repeated imports preserve existing tool content lock and process PATH");
var conflictArchive = Path.Combine(output, "same-version-conflict.mcutoolchain");
await LegacyZipFixture(archive, conflictArchive);
using (var zip = ZipFile.Open(conflictArchive, ZipArchiveMode.Update))
{
    zip.GetEntry("toolset.json")!.Delete();
    using var writer = new StreamWriter(zip.CreateEntry("toolset.json").Open());
    var original = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(newSource.RootDirectory, "test.gcc", "10.0.0", "toolset.json"));
    writer.Write(JsonSerializer.Serialize(original with
    {
        CompilerId = "different-compiler"
    }, JsonStore.Options));
}
await Reject(() => service.PreviewInstallAsync(conflictArchive), "TOOLS_VERSION_CONFLICT");
Check((await service.PreviewInstallAsync(archive)).AlreadyInstalled, "conflicting same-version import never overwrites installed identity");
var corruptDuplicate = Path.Combine(output, "corrupt-duplicate.mcutoolchain");
await LegacyZipFixture(archive, corruptDuplicate);
using (var zip = ZipFile.Open(corruptDuplicate, ZipArchiveMode.Update)) { using var text = new StreamWriter(zip.GetEntry("probe.exe")!.Open()); text.Write("corrupt"); }
var corruptDuplicatePreview = await service.PreviewInstallAsync(corruptDuplicate);
await Reject(() => service.InstallAsync(corruptDuplicatePreview), "TOOL_HASH");
var installedProbe = Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0", "probe.exe");
await File.WriteAllTextAsync(installedProbe, "damaged installed component");
await Reject(() => service.InstallAsync(preview), "TOOL_HASH");
var repairBackup = await new ToolEnvironmentService(catalog).RepairAsync(newEntry, archive, null);
Check(Directory.Exists(repairBackup) && (await service.InstallAsync(preview)).AlreadyInstalled, "canonical component package repairs damaged content through explicit repair with rollback backup");
var wrongExtension = Path.Combine(output, "unsupported.zip");
File.Copy(archive, wrongExtension);
await Reject(() => service.PreviewInstallAsync(wrongExtension), "TOOLS_ARCHIVE_FORMAT");
var unsupportedHost = Path.Combine(output, "unsupported-host.mcutoolchain");
File.Copy(conflictArchive, unsupportedHost);
using (var zip = ZipFile.Open(unsupportedHost, ZipArchiveMode.Update))
{
    zip.GetEntry("toolset.json")!.Delete();
    using var writer = new StreamWriter(zip.CreateEntry("toolset.json").Open());
    var original = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(newSource.RootDirectory, "test.gcc", "10.0.0", "toolset.json"));
    writer.Write(JsonSerializer.Serialize(original with
    {
        Host = "linux-x64"
    }, JsonStore.Options));
}
await Reject(() => service.PreviewInstallAsync(unsupportedHost), "TOOLS_IDENTITY");
var nextSource = await MakeTools(Path.Combine(output, "changed-source"), "11.0.0");
var changedArchive = Path.Combine(output, "changed.studioxtools");
await new ToolEnvironmentService(nextSource).ExportAsync(newEntry with { Version = "11.0.0" }, changedArchive, null);
await LegacyZipFixture(changedArchive, changedArchive);
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
    try
    {
        await service.PurgeAsync(purgeEntry, null);
        throw new InvalidOperationException("Expected occupied file failure");
    }
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
var activationEntry = final.Versions.Single(version => version.Installed && version.Version == "2.0.0");
var activationRoot = Path.Combine(catalog.RootDirectory, activationEntry.Id, activationEntry.Version);
var manifestBefore = await File.ReadAllBytesAsync(Path.Combine(activationRoot, "toolset.json"));
var cached = await catalog.ResolveAsync(activationEntry.Id, activationEntry.Version, activationEntry.CompilerId);
using (var lease = ToolUsageLease.Acquire(activationRoot))
{
    await Reject(() => service.SetEnabledAsync(activationEntry, false, null), "TOOLS_PROTECTED");
}
await service.SetEnabledAsync(activationEntry, false, null);
var disabled = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "2.0.0");
Check(!disabled.Enabled && disabled.StateText == "已禁用" && disabled.CanToggle && disabled.References.Count > 0,
    "referenced latest version may be disabled without removing files or selecting another version");
await Reject(() => catalog.ResolveAsync(disabled.Id, disabled.Version, disabled.CompilerId), "TOOLSET_DISABLED");
await Reject(() => new ProcessRunner().RunAsync(new(cached.Tool("gcc"), ["--version"], activationRoot, TimeSpan.FromSeconds(5))), "TOOLSET_DISABLED");
await new ToolEnvironmentService(catalog).VerifyAsync(new(disabled.Id, disabled.Version, disabled.Name, disabled.CompilerId, disabled.Bytes, disabled.Files, false, ""), null);
Check(!new ToolsetCatalog(catalog.RootDirectory, data).IsEnabled(disabled.Id, disabled.Version), "disabled exact identity survives catalog restart");
await Reject(() => service.DiagnoseDebugEnvironmentAsync(disabled, null), "TOOLSET_DISABLED");
await service.SetEnabledAsync(disabled, true, null);
var enabledResolved = await catalog.ResolveAsync(disabled.Id, disabled.Version, disabled.CompilerId);
var manifestAfter = await File.ReadAllBytesAsync(Path.Combine(activationRoot, "toolset.json"));
Check(enabledResolved.Fingerprint == cached.Fingerprint && manifestBefore.SequenceEqual(manifestAfter), "reenabling preserves original manifest and fingerprint");
var debugDiagnostic = await service.DiagnoseDebugEnvironmentAsync((await service.InspectAsync(null)).Versions.Single(version => version.Version == "2.0.0"), null);
Check(debugDiagnostic.Contains("不连接设备") && debugDiagnostic.Contains("exit=0"), "debug diagnostic uses fixed version commands and keeps original output");
var projectAfterActivation = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
Check(projectBytes.SequenceEqual(projectAfterActivation), "activation leaves project identity unchanged");
var inventory = new ToolInventoryService(catalog);
await File.WriteAllTextAsync(installedProbe, "unrelated damaged version");
var listing = await inventory.DescribeAsync();
Check(listing.Contains("10.0.0") && !listing.Contains("检查失败"), "startup inventory only reads manifests even when an unrelated installed payload is damaged");
using (var unrelatedLease = ToolUsageLease.Acquire(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0"), maintenance: true))
{
    var verifiedProject = await inventory.VerifyProjectAsync(project);
    Check(verifiedProject.Contains("✓") && verifiedProject.Contains("1.0.0") && !verifiedProject.Contains("10.0.0") && !verifiedProject.Contains("2.0.0"),
        "project verification ignores unrelated damaged and exclusively occupied versions");
}
await catalog.SetEnabledAsync("test.gcc", "1.0.0", false);
await Reject(() => inventory.VerifyProjectAsync(project), "TOOLSET_DISABLED");
await catalog.SetEnabledAsync("test.gcc", "1.0.0", true);
var primaryProbe = Path.Combine(catalog.RootDirectory, "test.gcc", "1.0.0", "probe.exe");
await File.WriteAllTextAsync(primaryProbe, "required damaged component");
await Reject(() => inventory.VerifyProjectAsync(project), "TOOL_HASH");
await File.WriteAllBytesAsync(primaryProbe, ninja);
var extraSource = await MakeTools(Path.Combine(output, "auxiliary-source"), "1.0.0");
var auxiliaryRoot = Path.Combine(catalog.RootDirectory, "test.extra", "1.0.0");
Directory.CreateDirectory(Path.GetDirectoryName(auxiliaryRoot)!);
Directory.Move(Path.Combine(extraSource.RootDirectory, "test.gcc", "1.0.0"), auxiliaryRoot);
var auxiliaryManifest = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(auxiliaryRoot, "toolset.json"));
await JsonStore.WriteAsync(Path.Combine(auxiliaryRoot, "toolset.json"), auxiliaryManifest with { Id = "test.extra" });
var multiProject = await MakeProject("multi-component", "1.0.0");
var multiManifest = await ProjectService.ReadAsync(multiProject);
await JsonStore.WriteAsync(Path.Combine(multiProject, ".studiox", "project.json"), multiManifest with
{
    DevelopmentComponents = [new("test.gcc", "1.0.0", "test-gcc"), new("test.extra", "1.0.0", "test-gcc", Purpose: "fixture auxiliary")]
});
var multiVerification = await inventory.VerifyProjectAsync(multiProject);
Check(multiVerification.Count(character => character == '✓') == 2 && multiVerification.Contains("test.extra") && !multiVerification.Contains("10.0.0"),
    "project checks include explicitly required auxiliary components while excluding unrelated installed versions");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try
    {
        await inventory.VerifyProjectAsync(project, token: canceled.Token);
        throw new InvalidOperationException("Expected cancellation");
    }
    catch (OperationCanceledException) { Check(true, "canceled project check never launches a component or leaves a use lease"); }
}
var projectAfterVerification = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
var lockAfterVerification = await File.ReadAllBytesAsync(lockFile);
Check(projectBytes.SequenceEqual(projectAfterVerification) && lockBytes.SequenceEqual(lockAfterVerification), "project verification leaves the project identity and content lock byte-identical");
await File.WriteAllBytesAsync(installedProbe, ninja);
var reviewed = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "10.0.0");
var removalProject = await MakeProject("explicit-removal", "10.0.0");
await service.RegisterProjectAsync(removalProject);
await Reject(() => service.RemoveAsync(reviewed, null), "TOOLS_CHANGED");
Check(Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0")), "a newly introduced dependency invalidates the reviewed deletion rather than removing the version");
reviewed = (await service.InspectAsync(null)).Versions.Single(version => version.Version == "10.0.0");
Check(reviewed.Latest && reviewed.CanRemove && reviewed.CanDelete && reviewed.References.Count > 0, "explicit removal is available for reviewed latest referenced versions");
using (var useLease = ToolUsageLease.Acquire(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0")))
{
    await Reject(() => service.DeleteAsync(reviewed, null), "TOOLS_PROTECTED");
}
var removedPath = await service.RemoveAsync(reviewed, null);
var removed = (await service.InspectAsync(null)).Versions.Single(version => version.RetirementId == Path.GetFileName(removedPath));
Check(removed.CanRestore && removed.CanDelete && removed.References.Count > 0, "explicit uninstall keeps a recoverable exact identity even with dependencies");
await Reject(() => catalog.ResolveAsync("test.gcc", "10.0.0", "test-gcc"), "TOOLSET_MISSING");
await service.RestoreAsync(removed);
Check((await catalog.ResolveAsync("test.gcc", "10.0.0", "test-gcc")).Manifest.Version == "10.0.0", "restoration verifies and recovers the explicitly removed latest version");
reviewed = (await service.InspectAsync(null)).Versions.Single(version => version.Installed && version.Version == "10.0.0");
var removalProjectBytes = await File.ReadAllBytesAsync(Path.Combine(removalProject, ".studiox", "project.json"));
await service.DeleteAsync(reviewed, null);
Check(!Directory.Exists(Path.Combine(catalog.RootDirectory, "test.gcc", "10.0.0"))
    && (await service.InspectAsync(null)).Versions.All(version => version.Version != "10.0.0"), "reviewed permanent deletion removes the installed latest version and recovery contents");
var removalProjectAfterDeletion = await File.ReadAllBytesAsync(Path.Combine(removalProject, ".studiox", "project.json"));
var originalProjectAfterDeletion = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox", "project.json"));
Check(removalProjectBytes.SequenceEqual(removalProjectAfterDeletion)
    && projectBytes.SequenceEqual(originalProjectAfterDeletion) && Environment.GetEnvironmentVariable("PATH") == originalPath,
    "explicit deletion preserves dependent project declarations, other projects and system PATH");
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

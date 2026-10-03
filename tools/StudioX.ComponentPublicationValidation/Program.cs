using System.Diagnostics;
using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--verify-public-component", var sourceUrl, var publisherKey, var componentId, var componentVersion, var publicOutput])
{
    var root = Path.GetFullPath(publicOutput);
    if (Directory.Exists(root)) throw new InvalidOperationException("Use a new public download evidence directory.");
    Directory.CreateDirectory(root);
    using var service = new DistributionService(root);
    var remote = await service.ReadAsync(sourceUrl, Path.GetFullPath(publisherKey));
    if (!remote.Verification.Contains("签名匹配")) throw new InvalidOperationException("Public directory is not verified.");
    var entry = remote.Catalog.Entries.Single(e => e.Kind == "tool" && e.Id == componentId && e.Version == componentVersion);
    var publicArchive = await service.DownloadAsync(remote, entry);
    var cached = await service.DownloadAsync(remote, entry);
    if (cached != publicArchive) throw new InvalidOperationException("Verified download cache changed.");
    var tools = new ToolsetCatalog(Path.Combine(root, "isolated-runtime/toolsets"));
    var manager = new ToolManagementService(tools, new PackRepository(Path.Combine(root, "packs")), new RecentProjectService(root), root);
    var publicPreview = await manager.PreviewInstallAsync(publicArchive);
    if (publicPreview.Id != entry.Id || publicPreview.Version != entry.Version || publicPreview.Bytes != entry.InstalledBytes ||
        !publicPreview.ArchiveSha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Public archive identity differs from the signed directory.");
    await manager.InstallAsync(publicPreview);
    var publicInstalled = await tools.ResolveAsync(publicPreview.Id, publicPreview.Version, publicPreview.CompilerId, default, true);
    if (!publicInstalled.Fingerprint.Equals(publicPreview.Fingerprint, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Public import changed the exact manifest bytes.");
    var publicDuplicate = await manager.InstallAsync(await manager.PreviewInstallAsync(publicArchive));
    if (!publicDuplicate.AlreadyInstalled) throw new InvalidOperationException("Public reimport replaced the component.");
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
    {
        status = "passed", scope = "public-download-and-import", hardware = false, sourceUrl,
        id = publicPreview.Id, version = publicPreview.Version, publicPreview.CompilerId, manifestSha256 = publicPreview.Fingerprint,
        archiveSha256 = publicPreview.ArchiveSha256, remote.CatalogSha256, remote.Verification,
        checks = new[] { "verified public catalog", "actual HTTPS Release download and SHA-256", "verified cache reuse", "archive identity and sizes", "full imported file verification", "duplicate import preserves component" }
    });
    Console.WriteLine("PASS IDE downloads, verifies and imports the public Release, then preserves it on repeated import");
    return;
}
if (args.Length is 4 or 5 && args[0] == "--verify-online-catalog")
{
    var catalogUrl = args[1];
    var publicKey = args[2];
    var onlineOutput = args[3];
    var expectedEntries = args.Length == 5 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
    var onlineRoot = Path.GetFullPath(onlineOutput);
    if (Directory.Exists(onlineRoot)) throw new InvalidOperationException("Use a new online evidence directory.");
    Directory.CreateDirectory(onlineRoot);
    using var service = new DistributionService(onlineRoot);
    var remote = await service.ReadAsync(catalogUrl, Path.GetFullPath(publicKey));
    if (!remote.Verification.Contains("签名匹配") || remote.Catalog.Entries.Length != expectedEntries)
        throw new InvalidOperationException("Public directory signature or explicit expected entry count differs.");
    await JsonStore.WriteAsync(Path.Combine(onlineRoot, "result.json"), new
    {
        success = true, network = true, hardware = false, source = catalogUrl,
        remote.CatalogSha256, remote.Verification, entries = remote.Catalog.Entries.Length
    });
    Console.WriteLine("PASS IDE verifies the public HTTPS directory against the pinned publisher key");
    return;
}
if (args.Length != 3) throw new ArgumentException("Usage: <repository> <candidate.json> <new output directory>");
var repository = Path.GetFullPath(args[0]);
var candidatePath = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new InvalidOperationException("Use a new validation directory.");
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool passed, string description)
{
    if (!passed) throw new InvalidOperationException(description);
    checks.Add(description);
    Console.WriteLine("PASS " + description);
}
var candidate = await JsonStore.ReadAsync<Candidate>(candidatePath);
var archive = PathBoundary.Resolve(Path.GetDirectoryName(candidatePath)!, candidate.Asset);
Check(candidate.CandidatePackage && !candidate.CatalogEligible && candidate.PublicationStatus == "pending-materials",
    "unapproved native package stays a local candidate and is absent from the public catalog");
var data = Path.Combine(output, "isolated-user-data");
using var distribution = new DistributionService(data);
var catalogPath = Path.Combine(repository, "catalog/catalog.json");
var key = Path.Combine(repository, "trust/publisher.pem");
var listing = await distribution.ReadAsync(catalogPath, key);
Check(listing.Verification.Contains("签名匹配"),
    "IDE distribution service verifies the real signed component catalog");
var tampered = Path.Combine(output, "tampered-catalog.json");
await File.WriteAllTextAsync(tampered, "{\"formatVersion\":1,\"publisher\":\"changed\",\"entries\":[]}");
File.Copy(catalogPath + ".sig", tampered + ".sig");
try { await distribution.ReadAsync(tampered, key); Check(false, "tampered catalog rejected"); }
catch (StudioXException ex) { Check(ex.Code == "CATALOG_SIGNATURE", "IDE rejects a changed catalog with the original signature"); }
using (var wrong = RSA.Create(3072))
{
    var wrongKey = Path.Combine(output, "wrong-publisher.pem");
    await File.WriteAllTextAsync(wrongKey, wrong.ExportSubjectPublicKeyInfoPem());
    try { await distribution.ReadAsync(catalogPath, wrongKey); Check(false, "wrong publisher rejected"); }
    catch (StudioXException ex) { Check(ex.Code == "CATALOG_SIGNATURE", "IDE rejects a catalog when the selected publisher key differs"); }
}
var toolCatalog = new ToolsetCatalog(Path.Combine(output, "isolated-runtime/toolsets"));
var management = new ToolManagementService(toolCatalog, new PackRepository(Path.Combine(data, "packs")), new RecentProjectService(data), data);
var preview = await management.PreviewInstallAsync(archive);
Check(preview.Id == candidate.Id && preview.Version == candidate.Version && preview.Host == candidate.Host &&
    preview.CompilerId == candidate.CompilerId && preview.Fingerprint.Equals(candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase) &&
    preview.ArchiveSha256.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase), "IDE preview reads the real candidate's immutable identity and archive hash");
Check(preview.Bytes == candidate.InstalledBytes && new FileInfo(archive).Length == candidate.DownloadBytes && candidate.DownloadBytes < 2L * 1024 * 1024 * 1024,
    "actual download and expanded sizes match the candidate receipt and GitHub asset limit");
await management.InstallAsync(preview);
var installed = await toolCatalog.ResolveAsync(candidate.Id, candidate.Version, candidate.CompilerId, default, true);
Check(installed.Fingerprint.Equals(candidate.ManifestSha256, StringComparison.OrdinalIgnoreCase), "fresh candidate import fully verifies all installed files without changing the manifest");
var duplicate = await management.InstallAsync(await management.PreviewInstallAsync(archive));
Check(duplicate.AlreadyInstalled, "reimport completely verifies the same native component and skips replacement");

// 只在隔离目录执行已完整校验的 SDCC，检查头文件、编译器、链接器和运行库，不访问设备。
Check(candidate.Id == "stc.sdcc" && installed.Manifest.Executables.ContainsKey("sdcc"), "native validation uses the explicitly selected SDCC component");
var native = Path.Combine(output, "native-test");
Directory.CreateDirectory(native);
var source = Path.Combine(native, "main.c");
await File.WriteAllTextAsync(source, "#include <stdint.h>\nvolatile uint32_t dividend = 123456789UL;\nvolatile uint32_t quotient;\nvoid main(void) { quotient = dividend / 37UL; for (;;) {} }\n");
var start = new ProcessStartInfo(installed.Tool("sdcc"))
{
    WorkingDirectory = native, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
    RedirectStandardOutput = true, RedirectStandardError = true
};
foreach (var argument in new[] { "-mmcs51", "--model-small", "--out-fmt-ihx", "-o", "firmware.ihx", "main.c" }) start.ArgumentList.Add(argument);
start.Environment.Clear();
foreach (var name in new[] { "SystemRoot", "WINDIR", "ComSpec" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
start.Environment["PATH"] = ToolsetEnvironment.Create(installed)["PATH"];
start.Environment["TEMP"] = start.Environment["TMP"] = native;
using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the verified compiler.");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try { await process.WaitForExitAsync(timeout.Token); }
catch { process.Kill(entireProcessTree: true); throw; }
await File.WriteAllTextAsync(Path.Combine(native, "compiler-stdout.txt"), await stdout);
await File.WriteAllTextAsync(Path.Combine(native, "compiler-stderr.txt"), await stderr);
var firmware = Path.Combine(native, "firmware.ihx");
Check(process.ExitCode == 0 && File.Exists(firmware) && new FileInfo(firmware).Length > 100,
    "imported SDCC really compiles and links a C51 program with stdint and division runtime support");
// 用同一导入组件再走 CMake/Ninja，避免仅调用编译器掩盖共享构建工具或路径问题。
var cmakeBuild = Path.Combine(native, "cmake-build");
var compiler = installed.Tool("sdcc").Replace('\\', '/').Replace("$", "\\$");
await File.WriteAllTextAsync(Path.Combine(native, "CMakeLists.txt"), $$"""
    cmake_minimum_required(VERSION 3.20)
    project(SdccComponentValidation NONE)
    add_custom_command(OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/firmware.ihx"
      COMMAND "{{compiler}}" -mmcs51 --model-small --out-fmt-ihx -o firmware.ihx "${CMAKE_CURRENT_SOURCE_DIR}/main.c"
      DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/main.c"
      WORKING_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}" VERBATIM)
    add_custom_target(firmware ALL DEPENDS "${CMAKE_CURRENT_BINARY_DIR}/firmware.ihx")
    """);
async Task RunCMakeAsync(string name, params string[] arguments)
{
    var command = new ProcessStartInfo(installed.Tool("cmake"))
    {
        WorkingDirectory = native, UseShellExecute = false, CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in arguments) command.ArgumentList.Add(argument);
    command.Environment.Clear();
    foreach (var variable in new[] { "SystemRoot", "WINDIR", "ComSpec" })
        if (Environment.GetEnvironmentVariable(variable) is { } value) command.Environment[variable] = value;
    command.Environment["PATH"] = ToolsetEnvironment.Create(installed)["PATH"];
    command.Environment["TEMP"] = command.Environment["TMP"] = native;
    using var running = Process.Start(command) ?? throw new InvalidOperationException("Cannot start verified CMake.");
    var outputTask = running.StandardOutput.ReadToEndAsync();
    var errorTask = running.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try { await running.WaitForExitAsync(deadline.Token); }
    catch { running.Kill(entireProcessTree: true); throw; }
    await File.WriteAllTextAsync(Path.Combine(native, name + "-stdout.txt"), await outputTask);
    await File.WriteAllTextAsync(Path.Combine(native, name + "-stderr.txt"), await errorTask);
    Check(running.ExitCode == 0, "imported CMake/Ninja " + name + " succeeds with the isolated component environment");
}
await RunCMakeAsync("configure", "-S", native, "-B", cmakeBuild, "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + installed.Tool("ninja"));
await RunCMakeAsync("build", "--build", cmakeBuild, "--verbose");
var directFirmwareBytes = await File.ReadAllBytesAsync(firmware);
var cmakeFirmwareBytes = await File.ReadAllBytesAsync(Path.Combine(cmakeBuild, "firmware.ihx"));
Check(directFirmwareBytes.SequenceEqual(cmakeFirmwareBytes),
    "CMake/Ninja firmware bytes match the direct SDCC compilation");
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
{
    status = "passed", scope = "offline-build-and-import", hardware = false, publicBinaryReleased = false,
    id = candidate.Id, version = candidate.Version, host = candidate.Host, compilerId = candidate.CompilerId,
    manifestSha256 = candidate.ManifestSha256, archiveSha256 = candidate.Sha256,
    archiveFiles = preview.Files, candidate.DownloadBytes, candidate.InstalledBytes,
    firmwareSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firmware))).ToLowerInvariant(), checks
});

internal sealed record Candidate(int FormatVersion, string Id, string Version, string Host, string CompilerId,
    string ManifestSha256, string Asset, string Sha256, long DownloadBytes, long InstalledBytes,
    [property: System.Text.Json.Serialization.JsonPropertyName("candidate")] bool CandidatePackage,
    bool CatalogEligible, string PublicationStatus);

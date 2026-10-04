using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is not [var mode, var packDirectory, var runtimeDirectory, var outputDirectory])
{
    Console.Error.WriteLine("Usage: StudioX.EspressifPackSetValidation <inspect|SDK-version> <packs-directory> <runtime-directory> <new-output-directory>");
    return 2;
}
var output = Path.GetFullPath(outputDirectory);
if (Directory.Exists(output)) { throw new IOException("Use a new validation output directory."); }
Directory.CreateDirectory(output);
var versions = new Dictionary<string, string>
{
    ["5.5.4"] = "0.1.1",
    ["5.5.5"] = "0.2.0",
    ["6.0.3"] = "0.3.0",
    ["6.1.0"] = "0.4.0"
};
string[] targets = ["esp32", "esp32s3", "esp32p4", "esp32c3", "esp32c5", "esp32c6"];
string[] templates = ["hello-world", "freertos"];
var repository = new PackRepository(Path.Combine(output, "repository"));
var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtimeDirectory), "toolsets"));
var checks = new List<string>();
var builds = new List<object>();
void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
}
foreach (var archive in Directory.GetFiles(Path.GetFullPath(packDirectory), "*.mcupack").Order(StringComparer.Ordinal))
{
    await repository.ImportAsync(archive);
}
var packs = (await repository.ListCatalogAsync()).Where(pack => pack.Manifest.Devices.Single().Espressif?.Framework == "esp-idf").ToArray();
Check(packs.Length == 24, "24 exact SDK/target packs pass full import validation");
Check(PackCatalogPolicy.SelectCurrentVersions(packs).Count == 24, "different SDK requirements coexist in the visible catalog");
var selection = new EspressifProjectVersionService(repository, catalog);
if (mode != "inspect" && !versions.ContainsKey(mode)) { throw new ArgumentException("Select an exact supported SDK version."); }
var buildService = new BuildService(catalog);
if (mode == "inspect")
{
    checks.AddRange(await RemotePackSetChecks.RunAsync(Path.GetFullPath(packDirectory), output));
}
foreach (var pack in packs.Where(pack => mode == "inspect" || pack.Manifest.Devices.Single().Espressif!.SdkVersion == mode))
{
    var device = pack.Manifest.Devices.Single();
    var sdk = device.Espressif!;
    Check(targets.Contains(sdk.Target) && versions.TryGetValue(sdk.SdkVersion, out var packVersion) && pack.Manifest.Version == packVersion,
        pack.Manifest.Id + "/" + pack.Manifest.Version + " has the expected SDK and target");
    Check(device.ToolsetVersion == sdk.SdkVersion && device.CompilerId == "esp-idf" && device.Templates.Select(template => template.Id).ToHashSet().SetEquals(templates),
        pack.Manifest.Id + "/" + pack.Manifest.Version + " declares two templates and an exact matching component");
    foreach (var template in device.Templates)
    {
        if (mode == "inspect")
        {
            var choices = await selection.ListAsync(pack, device.Id, template.Id);
            Check(choices.Count == 4 && choices.All(choice => choice.CanCreate) && choices.Select(choice => choice.SdkVersion).ToHashSet().SetEquals(versions.Keys),
                device.Id + "/" + template.Id + " can explicitly select every published SDK");
        }
        var name = sdk.Target + "_" + template.Id.Replace('-', '_');
        var projectDirectory = Path.Combine(output, "projects", sdk.SdkVersion, name);
        var project = await new ProjectService().CreateAsync(pack, device.Id, template.Id, name, projectDirectory);
        Check(project == await ProjectService.ReadAsync(projectDirectory) && project.ToolsetVersion == sdk.SdkVersion &&
            project.Espressif == new EspressifProjectSettings(sdk.Framework, sdk.Target, sdk.SdkVersion),
            name + "/" + sdk.SdkVersion + " keeps exact SDK metadata after creation");
        var evidencePath = Path.Combine(projectDirectory, "template-source.json");
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
        Check(evidence.RootElement.GetProperty("sdkVersion").GetString() == sdk.SdkVersion,
            name + "/" + sdk.SdkVersion + " keeps version-specific source evidence");
        foreach (var file in evidence.RootElement.GetProperty("filesSha256").EnumerateObject())
        {
            // 根工程名及目标默认配置由生成器显式修改；其它官方文件必须逐字节保留。
            if (file.Name is "CMakeLists.txt" or "sdkconfig.defaults")
            {
                continue;
            }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(PathBoundary.Resolve(projectDirectory, file.Name))));
            Check(hash.Equals(file.Value.GetString(), StringComparison.OrdinalIgnoreCase),
                name + "/" + sdk.SdkVersion + " preserves " + file.Name);
        }
        var cmake = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "CMakeLists.txt"));
        Check(cmake.Contains("include($ENV{IDF_PATH}/tools/cmake/project.cmake)", StringComparison.Ordinal) &&
            !cmake.Contains("E:/", StringComparison.OrdinalIgnoreCase) && !cmake.Contains("C:/", StringComparison.OrdinalIgnoreCase),
            name + "/" + sdk.SdkVersion + " uses the SDK native build without developer paths");
        if (mode == "inspect")
        {
            continue;
        }
        Console.WriteLine("BUILD " + sdk.SdkVersion + " " + name);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = await buildService.BuildAsync(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), report.Log);
        Check(report.Success, name + "/" + sdk.SdkVersion + " builds successfully: " + report.LogPath);
        var bin = report.Artifacts.Single(path => Path.GetFileName(path) == name + ".bin");
        builds.Add(new
        {
            sdkVersion = sdk.SdkVersion,
            componentVersion = device.ToolsetVersion,
            packId = pack.Manifest.Id,
            packVersion = pack.Manifest.Version,
            target = sdk.Target,
            template = template.Id,
            success = report.Success,
            exitCode = report.ExitCode,
            seconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
            firmwareBytes = new FileInfo(bin).Length,
            firmwareSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(bin))).ToLowerInvariant()
        });
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            formatVersion = 1,
            success = false,
            hardware = false,
            mode,
            checks,
            builds
        });
        Console.WriteLine("PASS " + sdk.SdkVersion + " " + name);
    }
}
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { formatVersion = 1, success = true, hardware = false, mode, checks, builds });
Console.WriteLine($"PASS: {checks.Count} checks, {builds.Count} real SDK builds");
return 0;

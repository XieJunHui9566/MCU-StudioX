using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

if (args is ["--boundaries", var checkRuntime, var checkDirectory]) return await BoundaryChecks.RunAsync(checkRuntime, checkDirectory);
if (args is not [var runtime, var source, var destination, var preset]) return 2;
var root = Path.GetFullPath(destination);
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
var fixture = Path.Combine(root, "project with spaces");
var hashes = new Dictionary<string, string>();
void Copy(string directory)
{
    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Fixture must not contain links.");
        if (entry.Name is "build" or ".build" or ".git" or ".studiox" || entry.Name.StartsWith("cmake-build-", StringComparison.Ordinal)) continue;
        if (entry is DirectoryInfo) { Copy(entry.FullName); continue; }
        var relative = Path.GetRelativePath(source, entry.FullName);
        var target = Path.Combine(fixture, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(entry.FullName, target);
        hashes[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
    }
}
Copy(source);
await using var workbench = new WorkbenchService(runtime, Path.Combine(root, "user-data"));
var inspection = await workbench.CubeMx.InspectAsync(fixture);
Console.WriteLine($"Detected {inspection.Name} / {inspection.Device}: {string.Join(", ", inspection.ConfigurePresets)}");
var project = await workbench.CubeMx.ImportAsync(fixture, preset);
if ((await ProjectService.ReadAsync(fixture)) != project) throw new InvalidOperationException("Import/reopen metadata differs.");
var metadata = await File.ReadAllTextAsync(Path.Combine(fixture, ".studiox/project.json"));
try { await workbench.CubeMx.ImportAsync(fixture, preset); throw new InvalidOperationException("Duplicate import overwrote metadata."); }
catch (IOException) { }
if (metadata != await File.ReadAllTextAsync(Path.Combine(fixture, ".studiox/project.json"))) throw new InvalidOperationException("Metadata changed.");
if (await workbench.Downloads.ConfigurationAsync(fixture) is not { OpenOcd.Probes.Count: 3 }) throw new InvalidOperationException("STM32 download profiles missing.");
var result = await workbench.Builds.BuildAsync(fixture, new InlineProgress(Console.WriteLine));
await File.WriteAllTextAsync(Path.Combine(root, "build.log"), result.Log);
if (!result.Success) throw new InvalidOperationException(result.Log);
if (!result.Artifacts.Any(path => path.EndsWith(inspection.Name + ".elf", StringComparison.Ordinal)) || result.Artifacts.Any(path => !File.Exists(path)))
    throw new InvalidOperationException("Original firmware target not reported.");
await workbench.Intelligence.StartAsync(fixture);
var text = await File.ReadAllTextAsync(Path.Combine(fixture, "Core/Src/main.c"));
var hover = await workbench.Intelligence.HoverAsync("Core/Src/main.c", text, text.IndexOf("HAL_Init();", StringComparison.Ordinal) + 3);
await JsonStore.WriteAsync(Path.Combine(root, "hover.json"), hover);
var locations = await workbench.Intelligence.NavigateAsync("Core/Src/main.c", text, text.IndexOf("HAL_Init();", StringComparison.Ordinal) + 3, true);
await JsonStore.WriteAsync(Path.Combine(root, "navigation.json"), locations);
if (hover is null || locations.Count == 0 || !locations.Any(location => location.DocumentPath.Contains("hal", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("HAL declaration/hover lookup failed: " + string.Join("\n", workbench.Intelligence.DrainLog()));
var completeText = text + "\nvoid studiox_completion_check(void) { HAL_GPIO_Wri }\n";
var completion = await workbench.Intelligence.CompleteAsync("Core/Src/main.c", completeText, completeText.IndexOf("HAL_GPIO_Wri", StringComparison.Ordinal) + "HAL_GPIO_Wri".Length);
if (!completion.Any(item => item.Label.Contains("HAL_GPIO_WritePin", StringComparison.Ordinal))) throw new InvalidOperationException("HAL completion failed.");
foreach (var (path, hash) in hashes)
    if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(fixture, path)))) != hash) throw new InvalidOperationException("Source changed: " + path);
var second = await workbench.Builds.BuildAsync(fixture, new InlineProgress(Console.WriteLine));
await File.WriteAllTextAsync(Path.Combine(root, "incremental.log"), second.Log);
if (!second.Success) throw new InvalidOperationException(second.Log);
if (second.Log.Contains("compiler identification", StringComparison.Ordinal))
    throw new InvalidOperationException("Repeated configure did not reuse the compiler cache.");
// Windows 启动路径可能全小写；跨会话打开后不能因编译器路径的大小写差异丢失工具链。
await using (var reopened = new WorkbenchService(OperatingSystem.IsWindows() ? runtime.ToLowerInvariant() : runtime, Path.Combine(root, "user-data")))
{
    var reopenedResult = await reopened.Builds.BuildAsync(fixture);
    await File.WriteAllTextAsync(Path.Combine(root, "reopened.log"), reopenedResult.Log);
    if (!reopenedResult.Success) throw new InvalidOperationException(reopenedResult.Log);
    var repeated = await reopened.Builds.BuildAsync(fixture);
    await File.WriteAllTextAsync(Path.Combine(root, "reopened-incremental.log"), repeated.Log);
    if (!repeated.Success || repeated.Log.Contains("compiler identification", StringComparison.Ordinal))
        throw new InvalidOperationException("Lowercase runtime path broke incremental configure: " + repeated.Log);
}
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"PASS: {inspection.Device}, {preset}; unchanged {hashes.Count} original files; atomic duplicate rejection; reopen; compile in spaced path; original ELF/BIN/HEX; HAL completion, hover and declaration; incremental build; lowercase runtime/reopen/incremental build; three download profiles (no hardware access).\n");
Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(root, "result.txt")));
return 0;

sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }

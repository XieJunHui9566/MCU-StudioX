using System.Text.Json;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

var offline = args is ["--offline", _];
if (!offline && args.Length != 3)
{
    throw new ArgumentException("portable-validation --offline <新目录> 或 <新目录> <runtime> <Linux pack>");
}
var root = Path.GetFullPath(offline ? args[1] : args[0]);
if (Directory.Exists(root))
{
    throw new InvalidOperationException("验证目录必须不存在。");
}
Directory.CreateDirectory(root);
var runtime = offline ? Path.Combine(root, "runtime") : Path.GetFullPath(args[1]);
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string code)
{
    try
    {
        await action();
    }
    catch (StudioXException error) when (error.Code == code)
    {
        Check(true, "reject " + code);
        return;
    }
    throw new InvalidOperationException("Expected " + code);
}

var recentCase = new RecentProjectService(Path.Combine(root, "recent-case-data"));
await recentCase.RememberAsync("case_one", Path.Combine(root, "case-project"));
await recentCase.RememberAsync("case_two", Path.Combine(root, "CASE-PROJECT"));
Check((await recentCase.LoadAsync()).Count == (OperatingSystem.IsWindows() ? 1 : 2), "recent project path identity follows native case rules");

var linuxNeed = new DevelopmentComponentRequirement("arm.gnu", "1.0.0", "fixture", "linux-x64");
DevelopmentComponentRequirements.Validate([linuxNeed]);
Check(DevelopmentComponentRequirements.Primary("arm.gnu", "1.0.0", "fixture", [linuxNeed]).Host == "linux-x64", "explicit Linux requirement keeps its host");
Check(DevelopmentComponentRequirements.Primary("arm.gnu", "1.0.0", "fixture", null).Host == "win-x64", "legacy metadata remains Windows and is not silently migrated");
await Reject(() =>
{
    DevelopmentComponentRequirements.Merge([linuxNeed], [linuxNeed with { Host = "win-x64" }]);
    return Task.CompletedTask;
}, "DEVELOPMENT_COMPONENT_REQUIREMENT");
await Reject(() =>
{
    DevelopmentComponentRequirements.Validate([linuxNeed with { Host = "linux-arm64" }]);
    return Task.CompletedTask;
}, "DEVELOPMENT_COMPONENT_REQUIREMENT");

var incompatible = HostPlatform.Current == "win-x64" ? "linux-x64" : "win-x64";
var wrongTools = Path.Combine(root, "wrong-host/toolsets");
await JsonStore.WriteAsync(Path.Combine(wrongTools, "arm.gnu/1.0.0/toolset.json"),
    new ToolsetManifest(1, "arm.gnu", "1.0.0", incompatible, "fixture", new(), new()));
await Reject(async () => await new ToolsetCatalog(wrongTools).ResolveAsync("arm.gnu", "1.0.0", "fixture"), "TOOLSET_INCOMPATIBLE");

var fixture = new ProjectManifest(1, "portable_fixture", "fixture.pack", "1.0.0", new string('a', 64),
    "fixture", "hal", "arm.gnu", "1.0.0", "fixture", DevelopmentComponents: [linuxNeed with { Host = HostPlatform.Current }]);
foreach (var name in new[] { "first", "second", "wrong-project-host" })
{
    var directory = Path.Combine(root, name);
    await JsonStore.WriteAsync(Path.Combine(directory, ".studiox/project.json"), fixture with
    {
        DevelopmentComponents = [linuxNeed with { Host = name == "wrong-project-host" ? incompatible : HostPlatform.Current }]
    });
    Directory.CreateDirectory(Path.Combine(directory, "src"));
    await File.WriteAllTextAsync(Path.Combine(directory, "src/main.c"), "int main(void) { return 0; }\n");
}
await using (var workspace = new PortableWorkspaceService(runtime, Path.Combine(root, "user-data")))
{
    await workspace.OpenAsync(Path.Combine(root, "first"));
    var first = await workspace.ReadAsync("src/main.c");
    var edited = first.Source.Text + "// 中文编辑与文件事务\n";
    var saved = await workspace.SaveAsync(first, edited);
    Check((await workspace.ReadAsync("src/main.c")).Source.Text == edited, "UTF-8 editor save round-trips through application service");
    await File.WriteAllTextAsync(Path.Combine(root, "second/src/main.c"), edited);
    await workspace.OpenAsync(Path.Combine(root, "second"));
    await Reject(async () => await workspace.SaveAsync(saved, "unexpected"), "WORKSPACE_DOCUMENT");
    Check(await File.ReadAllTextAsync(Path.Combine(root, "second/src/main.c")) == edited, "switching project preserves identical-path identical-content files");
    var second = await workspace.ReadAsync("src/main.c");
    await File.AppendAllTextAsync(Path.Combine(root, "second/src/main.c"), "// external edit\n");
    await Reject(async () => await workspace.SaveAsync(second, "unexpected"), "EDITOR_FILE_CHANGED");
    Check((await File.ReadAllTextAsync(Path.Combine(root, "second/src/main.c"))).Contains("external edit", StringComparison.Ordinal), "external disk edits survive rejected saves");
    await Reject(async () => await workspace.ReadAsync("../outside.c"), "PATH_UNSAFE");
    await workspace.OpenAsync(Path.Combine(root, "wrong-project-host"));
    await Reject(async () => await workspace.BuildAsync(), "TOOLS_HOST");
    Check(!File.Exists(Path.Combine(root, "wrong-project-host/.studiox/toolchain.lock.json")), "host mismatch does not establish a tool lock");
    await workspace.CloseAsync();
    Check(workspace.Project is null, "close project clears application workspace");
    await Reject(async () => await workspace.ReadAsync("src/main.c"), "PROJECT_REQUIRED");
    await workspace.SelectThemeAsync(ThemeService.Light);
    Check((await workspace.LoadThemeAsync()).Id == ThemeService.Light.Id, "shared light theme persists in isolated user data");
    Check((await workspace.RecentAsync()).Count == 3, "recent projects are served without scanning SDKs");
}

if (!offline)
{
    Check(HostPlatform.Current == "linux-x64", "native Linux x64 validation host");
    var repository = new PackRepository(Path.Combine(root, "packs"));
    var pack = await repository.ImportAsync(args[2]);
    await using (var creation = new PortableWorkspaceService(runtime, Path.Combine(root, "creation-data")))
    {
        await creation.ImportPackAsync(args[2]);
        var selected = (await creation.PacksAsync()).Single();
        var device = selected.Devices.Single(item => item.Id == "STM32F407ZG");
        Check(device.Templates.All(item => item.HostCompatible), "new-project selector uses explicit Linux template hosts");
        var createdPath = Path.Combine(root, "created-through-app");
        await creation.CreateAsync(selected, device.Id, "hal", "app_created", createdPath);
        Check(File.Exists(Path.Combine(createdPath, ".studiox/project.json")) && Directory.Exists(Path.Combine(createdPath, ".git")), "application creates a verified project and initializes private native Git");
        await Reject(async () => await creation.CreateAsync(selected with { ContentHash = new string('0', 64) }, device.Id, "hal", "stale", Path.Combine(root, "stale")), "PACK_CHANGED");
    }
    foreach (var template in new[] { "hal", "hal-freertos" })
    {
        var project = Path.Combine(root, "f407-" + template);
        var created = await new ProjectService().CreateAsync(pack, "STM32F407ZG", template, "linux_f407_" + template.Replace('-', '_'), project);
        Check(created.DevelopmentComponents?.Single().Host == "linux-x64", "generated " + template + " project preserves explicit Linux metadata");
        await using var workspace = new PortableWorkspaceService(runtime, Path.Combine(root, "data-" + template));
        await workspace.OpenAsync(project);
        var result = await workspace.BuildAsync(new Progress<string>(Console.WriteLine));
        await File.WriteAllTextAsync(Path.Combine(root, template + ".log"), result.Log);
        Check(result.Success && result.Artifacts.Count == 4, "application BuildService produces HAL/RTOS firmware: " + template);
        var locked = await JsonStore.ReadAsync<DevelopmentComponentLock>(Path.Combine(project, DevelopmentComponentLock.RelativePath));
        Check(locked.Components.Single().Host == "linux-x64", "component content lock records Linux host: " + template);
        var document = await workspace.ReadAsync("src/main.c");
        await workspace.SaveAsync(document, document.Source.Text + "\n");
        Check((await workspace.BuildAsync()).Success, "locked component and incremental rebuild succeed: " + template);
    }
}
await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
{
    success = true,
    host = HostPlatform.Current,
    hardware = false,
    checks
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS " + checks.Count + " portable workspace and host checks");

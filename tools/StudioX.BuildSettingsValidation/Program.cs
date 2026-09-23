using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

if (args is not [var runtime, var packs, var output]) throw new ArgumentException("Usage: BuildSettingsValidation <tool-runtime> <packs> <new-output>");
var root = Path.GetFullPath(output);
if (Directory.Exists(root)) throw new ArgumentException("Use a new output directory.");
var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
var builds = new BuildService(catalog);
var repository = new PackRepository(Path.Combine(root, "packs"));
var cases = new[]
{
    ("STM32-0.1.1/studiox.stm32f103-0.1.1.mcupack", "STM32F103C8", "spl"),
    ("CH32V307-0.1.1/wch.ch32v307-0.1.1.mcupack", "CH32V307VCT6", "spl")
};
foreach (var (archive, device, template) in cases)
{
    var pack = await repository.ImportAsync(Path.Combine(Path.GetFullPath(packs), archive));
    var project = Path.Combine(root, "projects", device + " with spaces");
    await new ProjectService().CreateAsync(pack, device, template, "settings_check", project);
    var cmakePath = Path.Combine(project, "CMakeLists.txt");
    await File.AppendAllTextAsync(cmakePath, "\ntarget_sources(firmware PRIVATE src/check.cpp)\ntarget_compile_options(firmware PRIVATE -O3 -g1)\nset_source_files_properties(src/main.c PROPERTIES COMPILE_OPTIONS \"-O1;-g2\")\n");
    await File.WriteAllTextAsync(Path.Combine(project, "src/check.cpp"), "extern \"C\" int settings_cpp(int value) { return value * 3 + 1; }\n");
    var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(cmakePath));
    Check(await builds.LoadSettingsAsync(project) == new ProjectBuildSettings(), "Default settings for existing projects");
    var settings = new ProjectBuildSettings(Optimization: CompilerOptimization.O2, DebugInfo: CompilerDebugInfo.Full);
    await builds.SaveSettingsAsync(project, settings);
    Check(await builds.LoadSettingsAsync(project) == settings, "Persistence");
    var report = await builds.BuildAsync(project);
    Check(report.Success, report.Log);
    await File.WriteAllTextAsync(Path.Combine(root, device + "-O2.log"), report.Log);
    CheckCommands(project, "-O2", "-g3");
    var receiptPath = Path.Combine(project, ".build/studiox-build-receipt.json");
    Check(File.Exists(receiptPath), "Successful build creates receipt");
    report = await builds.ConfigureAsync(project);
    Check(report.Success && !report.Log.Contains("重建 CMake 缓存", StringComparison.Ordinal), "Unchanged settings reuse configuration");
    CheckCommands(project, "-O2", "-g3");
    report = await builds.BuildAsync(project);
    Check(report.Success && File.Exists(receiptPath), "Rebuild with unchanged settings");
    await builds.SaveSettingsAsync(project, new(Optimization: CompilerOptimization.Os, DebugInfo: CompilerDebugInfo.None));
    Check(!File.Exists(receiptPath), "Changing settings invalidates old build receipt");
    try { await HardwareDebugPreparer.PrepareAsync(project, new OpenOcdService(catalog)); throw new Exception("Expected disabled symbols rejection"); }
    catch (StudioXException ex) when (ex.Code == "DEBUG_SYMBOLS") { }
    report = await builds.BuildAsync(project);
    Check(report.Success, report.Log); CheckCommands(project, "-Os", "-g0");
    await File.WriteAllTextAsync(Path.Combine(root, device + "-Os.log"), report.Log);
    var tools = await catalog.ResolveAsync(pack.Manifest.Devices.Single(d => d.Id == device).ToolsetId, "1.0.0", pack.Manifest.Devices.Single(d => d.Id == device).CompilerId);
    var sections = await new ProcessRunner().RunAsync(new(tools.Tool("readelf"), ["-S", Path.Combine(project, ".build/firmware.elf")], project,
        TimeSpan.FromSeconds(15), ToolsetEnvironment.Create(tools)));
    Check(sections.Success && !sections.StandardOutput.Contains(".debug_info", StringComparison.Ordinal), "-g0 removes compilation debug information");
    await builds.SaveSettingsAsync(project, new());
    report = await builds.ConfigureAsync(project);
    Check(report.Success, report.Log);
    var defaultCommands = await File.ReadAllTextAsync(Path.Combine(project, ".build/compile_commands.json"));
    Check(!defaultCommands.Contains(" -g0 ", StringComparison.Ordinal), "Reset drops previously injected flags");
    Check(SHA256.HashData(await File.ReadAllBytesAsync(cmakePath)).SequenceEqual(sourceHash), "Original CMake remains unchanged");
    try { await builds.SaveSettingsAsync(project, new(Optimization: (CompilerOptimization)99)); throw new Exception("Expected invalid settings rejection"); }
    catch (StudioXException ex) when (ex.Code == "BUILD_SETTINGS") { }
    if (device == "STM32F103C8")
    {
        var cmakeSource = await File.ReadAllTextAsync(cmakePath);
        await File.AppendAllTextAsync(cmakePath, "\nset(CMAKE_C_COMPILE_OBJECT \"<CMAKE_C_COMPILER> <DEFINES> <INCLUDES> <FLAGS> -O0 -o <OBJECT> -c <SOURCE>\")\n");
        await builds.SaveSettingsAsync(project, new(Optimization: CompilerOptimization.O2));
        try { await builds.ConfigureAsync(project); throw new Exception("Expected custom rule rejection"); }
        catch (StudioXException ex) when (ex.Code == "BUILD_SETTINGS_NOT_APPLIED") { }
        Check(!File.Exists(receiptPath) && (await File.ReadAllTextAsync(Path.Combine(project, ".build/studiox-build.log"))).Contains("未开始编译", StringComparison.Ordinal), "Custom rule rejection persists diagnostics and leaves no receipt");
        await File.WriteAllTextAsync(cmakePath, cmakeSource);
        await builds.SaveSettingsAsync(project, new());
        Console.WriteLine("PASS conflicting custom rule stops configuration and preserves diagnostic log");
    }
    Console.WriteLine("PASS " + device + ": C/C++/ASM flags, conflicting template/target/source options, real build, symbols, reset, persistence, receipt invalidation and unchanged CMake");
}

// Import fixture uses the generated F103 sources, but an independent CubeMX-style toolchain and preset.
var cubeRoot = Path.Combine(root, "projects", "CubeMX with spaces");
var original = Path.Combine(root, "projects", "STM32F103C8 with spaces");
foreach (var source in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(original, source);
    if (relative.StartsWith(".build") || relative.StartsWith(".studiox")) continue;
    var target = Path.Combine(cubeRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
}
Directory.CreateDirectory(Path.Combine(cubeRoot, "cmake/stm32cubemx"));
await File.WriteAllTextAsync(Path.Combine(cubeRoot, "cmake/stm32cubemx/CMakeLists.txt"), "# Isolated import fixture\n");
await File.WriteAllTextAsync(Path.Combine(cubeRoot, "cmake/gcc-arm-none-eabi.cmake"), "set(CMAKE_SYSTEM_NAME Generic)\nset(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)\nset(CMAKE_C_COMPILER arm-none-eabi-gcc)\nset(CMAKE_CXX_COMPILER arm-none-eabi-g++)\nset(CMAKE_ASM_COMPILER arm-none-eabi-gcc)\n");
await File.WriteAllTextAsync(Path.Combine(cubeRoot, "settings.ioc"), "Mcu.CPN=STM32F103C8T6\nProjectManager.ProjectName=settings\n");
await File.WriteAllTextAsync(Path.Combine(cubeRoot, "user-include.cmake"), "set(USER_INCLUDE_PRESERVED YES CACHE STRING \"\" FORCE)\n");
await File.WriteAllTextAsync(Path.Combine(cubeRoot, "CMakePresets.json"), """
{"version":3,"configurePresets":[{"name":"Debug","generator":"Ninja","cacheVariables":{"CMAKE_BUILD_TYPE":"Debug","CMAKE_PROJECT_INCLUDE":"${sourceDir}/user-include.cmake"}}]}
""");
await new CubeMxImportService(catalog).ImportAsync(cubeRoot, "Debug");
await builds.SaveSettingsAsync(cubeRoot, new(Optimization: CompilerOptimization.Og, DebugInfo: CompilerDebugInfo.Standard));
var cubeReport = await builds.BuildAsync(cubeRoot);
Check(cubeReport.Success, cubeReport.Log); CheckCommands(cubeRoot, "-Og", "-g2");
await File.WriteAllTextAsync(Path.Combine(root, "CubeMX.log"), cubeReport.Log);
Check((await File.ReadAllTextAsync(Path.Combine(cubeRoot, ".build/CMakeCache.txt"))).Contains("USER_INCLUDE_PRESERVED:STRING=YES", StringComparison.Ordinal), "Preserve preset project include");
Console.WriteLine("PASS CubeMX preset, custom project include preservation and real compilation");
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: STM32F103, CH32V307 and CubeMX compiler settings; real GCC builds, C/C++/ASM overrides, symbol removal, reset, persistence, cache reuse and build receipt invalidation. No hardware access.\n");

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void CheckCommands(string project, string optimization, string debug)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, ".build/compile_commands.json")));
    foreach (var entry in document.RootElement.EnumerateArray())
    {
        var command = entry.GetProperty("command").GetString()!;
        Check(command.Contains(" " + optimization + " " + debug + " ", StringComparison.Ordinal), command);
    }
}

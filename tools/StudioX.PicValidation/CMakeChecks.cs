namespace StudioX.PicValidation;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class CMakeChecks
{
    public static async Task RunAsync(string output, string runtime, string archive)
    {
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use a new evidence directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
        var pack = await services.Packs.ImportAsync(archive);
        var project = Path.Combine(output, "中文 project with spaces");
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-basic", "PicCMake", project);
        Check(File.Exists(Path.Combine(project, "CMakeLists.txt")), "new PIC project has a standalone CMake entry");
        var health = await services.ProjectHealth.InspectAsync(project);
        Check(health.CanBuild, "PIC CMake and XC8 components pass health checks");
        var configured = await services.Builds.ConfigureAsync(project);
        Check(configured.Success && !File.Exists(Path.Combine(project, ".studiox/pic-build.json")), "configure only does not publish firmware");
        var main = Path.Combine(project, "src/main.c");
        await File.WriteAllTextAsync(main, "#include <xc.h>\n#include \"value.h\"\n#pragma config FOSC=XT, WDTE=OFF, PWRTE=ON, BOREN=OFF, LVP=OFF, CPD=OFF, WRT=OFF, CP=OFF\nvolatile unsigned int counter;\nvoid main(void){for(;;){counter=next_value(counter);}}\n");
        Directory.CreateDirectory(Path.Combine(project, "extra headers"));
        await File.WriteAllTextAsync(Path.Combine(project, "extra headers/value.h"), "unsigned int next_value(unsigned int value);\n");
        await File.WriteAllTextAsync(Path.Combine(project, "src/value.c"), "#include \"value.h\"\n#ifndef DELTA\n#error CMake definition missing\n#endif\nunsigned int next_value(unsigned int value){return value+DELTA;}\n");
        await File.WriteAllTextAsync(Path.Combine(project, "src/unregistered.c"), "#error This file must not be compiled\n");
        var cmake = Path.Combine(project, "CMakeLists.txt");
        var text = (await File.ReadAllTextAsync(cmake)).Replace("pic_xc8_firmware(app)",
            "target_sources(app INTERFACE src/value.c)\ntarget_include_directories(app INTERFACE \"${CMAKE_CURRENT_SOURCE_DIR}/extra headers\")\ntarget_compile_definitions(app INTERFACE DELTA=3)\npic_xc8_firmware(app)");
        await File.WriteAllTextAsync(cmake, text);
        var build = await services.Builds.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(output, "release-build.log"), build.Log);
        Check(build.Success, "Ninja invokes real XC8 with multiple C files, includes and definitions");
        Check(build.Log.Contains("ninja", StringComparison.OrdinalIgnoreCase) || build.Log.Contains("[1/1]", StringComparison.Ordinal), "build executes the generated Ninja graph");
        var firmware = await services.Builds.Pic.PreviewAsync(project);
        Check(firmware.Debugger == "none" && File.Exists(firmware.ElfPath), "release receipt binds HEX and ELF");
        var flags = await services.Builds.Pic.AnalysisFlagsAsync(project);
        Check(flags.Contains("-DDELTA=3") && flags.Any(flag => flag.Contains("extra headers", StringComparison.Ordinal) && !flag.Contains("build-stage", StringComparison.Ordinal)),
            "CMake definitions and mapped include paths feed the code editor");
        var debugBuild = await services.Builds.BuildPicDebugAsync(project);
        await File.WriteAllTextAsync(Path.Combine(output, "debug-build.log"), debugBuild.Log);
        Check(debugBuild.Success && debugBuild.Log.Contains("-mdebugger=pickit3", StringComparison.Ordinal), "debug build reserves real PICkit3 executive resources");
        Check((await services.Builds.Pic.PreviewAsync(project)).Debugger == "pickit3", "debug receipt cannot be confused with release");
        Check(Directory.GetDirectories(Path.Combine(project, ".build/pic")).Select(Path.GetFileName).SequenceEqual(["latest"]) &&
            firmware.HexPath.Contains(Path.Combine("pic", "latest"), StringComparison.Ordinal), "configure, release and debug builds reuse only latest");
        await File.WriteAllTextAsync(cmake, text.Replace("target_sources(app INTERFACE src/value.c)", ""));
        var missing = await services.Builds.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(output, "removed-source.log"), missing.Log);
        Check(!missing.Success && !File.Exists(Path.Combine(project, ".studiox/pic-build.json")), "removing a registered source reaches the XC8 linker and invalidates old firmware");
        await File.WriteAllTextAsync(cmake, text);
        var recovered = await services.Builds.BuildPicDebugAsync(project);
        Check(recovered.Success, "CMake source repair recovers the build");
        Check(Directory.GetDirectories(Path.Combine(project, ".build/pic")).Length == 1, "failed and recovered builds do not accumulate output directories");
        File.Delete(cmake);
        try
        {
            await ProjectService.ReadAsync(project);
            throw new InvalidOperationException("Old PIC accepted");
        }
        catch (StudioXException error) when (error.Code == "PIC_CMAKE_REQUIRED") { Check(true, "legacy direct-XC8 projects are rejected without migration"); }
        await File.WriteAllTextAsync(cmake, text);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks,
            project
        });
    }
}

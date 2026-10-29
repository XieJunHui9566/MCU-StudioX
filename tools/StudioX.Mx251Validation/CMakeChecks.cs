using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.BuildConfiguration;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class CMakeChecks
{
    internal static async Task RunAsync(string archive, string tools, string output, string userSource)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new validation directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new Exception(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        var project = Path.Combine(output, "工程 空格");
        var manifest = await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "cmake", project);
        Check(ProjectCapabilities.For(manifest).Configure && ProjectCapabilities.For(manifest).SourceRegistration, "CMake and source registration enabled");
        var builder = new BuildService(new ToolsetCatalog(tools));
        var main = Path.Combine(project, "src/main.c");
        var cmakePath = Path.Combine(project, "CMakeLists.txt");
        var cmake = await File.ReadAllTextAsync(cmakePath);
        await File.WriteAllTextAsync(main, await File.ReadAllTextAsync(userSource));
        async Task<BuildReport> Build(string name, bool success, string? diagnostic = null)
        {
            var result = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), result.Log);
            Check(result.Success == success, name);
            if (diagnostic is not null)
            {
                Check(result.Log.Contains(diagnostic), name + " original diagnostic");
            }
            Check(File.Exists(Path.Combine(project, ".build/firmware.hex")) == success, name + " public image state");
            return result;
        }
        var configured = await builder.ConfigureAsync(project);
        Check(configured.Success && !File.Exists(Path.Combine(project, ".build/firmware.hex")), "configure-only does not publish a firmware");
        await Build("user-source-unchanged", true);
        var initial = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex")));
        await Build("repeat-build", true);
        var repeated = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex")));
        Check(initial.SequenceEqual(repeated), "repeat CMake build has same image");
        Check(Directory.GetDirectories(Path.Combine(project, ".build/mx251")).Select(Path.GetFileName).SequenceEqual(new[] { "latest" }), "one current build directory");
        Directory.CreateDirectory(Path.Combine(project, "extra headers"));
        await File.WriteAllTextAsync(Path.Combine(project, "extra headers/check.h"), "#ifndef CM_FEATURE\n#error missing CMake global define\n#endif\nunsigned int extra_value(void);\n");
        await File.WriteAllTextAsync(Path.Combine(project, "src/extra.c"), "#include \"check.h\"\n#ifndef PER_FILE\n#error missing CMake source define\n#endif\nunsigned int extra_value(void){return CM_FEATURE+PER_FILE;}\n");
        await File.WriteAllTextAsync(main, "#include \"AI8051U.h\"\n#include \"AI8051U_Delay.h\"\n#include \"check.h\"\nvolatile unsigned int result;\nvoid main(void){result=extra_value();Delay_MS(0);for(;;){}}\n");
        var expanded = cmake.Replace("mx251_firmware(app)", "target_include_directories(app INTERFACE \"extra headers\")\ntarget_compile_definitions(app INTERFACE CM_FEATURE=7)\nset_source_files_properties(src/extra.c PROPERTIES COMPILE_DEFINITIONS PER_FILE=3)\nmx251_firmware(app)");
        await File.WriteAllTextAsync(cmakePath, expanded);
        var files = new ProjectFileService();
        var registration = new SourceRegistrationService(files);
        var context = await registration.ReadAsync(project, "CMakeLists.txt", []);
        var plan = registration.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/extra.c")]);
        Check(plan.Change.After.Contains("INTERFACE \"src/extra.c\""), "source registration preserves INTERFACE scope");
        await File.WriteAllTextAsync(cmakePath, plan.Change.After);
        expanded = plan.Change.After;
        await Build("cmake-new-source-header-and-defines", true);
        Check((await File.ReadAllTextAsync(Path.Combine(project, ".build/mx251/latest/commands.json"))).Contains("-Iextra headers"), "actual compiler receives include directory");
        await File.WriteAllTextAsync(cmakePath, expanded.Replace("\"src/extra.c\"", ""));
        await Build("cmake-source-removal", false, "extra_value");
        await File.WriteAllTextAsync(cmakePath, expanded.Replace("target_include_directories(app INTERFACE \"extra headers\")", ""));
        await Build("cmake-include-removal", false, "check.h");
        await File.WriteAllTextAsync(cmakePath, expanded.Replace("mx251_firmware(app)", "target_compile_options(app INTERFACE -funsupported)\nmx251_firmware(app)"));
        await Build("unsupported-option-diagnostic", false, "INTERFACE_COMPILE_OPTIONS");
        await File.WriteAllTextAsync(cmakePath, expanded);
        await Build("recovery", true);
        var memory = await new BuildMemoryService(new ToolsetCatalog(tools)).ReadAsync(project);
        Check(memory.Targets.Count == 1 && memory.Targets[0].Regions.Count == 3, "CMake build keeps memory analysis");
        await using (var editor = new CodeIntelligenceService(Path.GetDirectoryName(Path.GetFullPath(tools))!, Path.Combine(output, "language-data")))
        {
            await editor.StartAsync(project);
            foreach (var relative in new[] { "src/main.c", "src/extra.c" })
            {
                var text = await File.ReadAllTextAsync(Path.Combine(project, relative));
                await editor.SynchronizeDiagnosticsAsync(relative, text, [new(relative, text)]);
                CodeDiagnosticBatch? batch = null;
                for (var i = 0; i < 100 && batch is null; i++)
                {
                    batch = editor.GetDiagnostics().FirstOrDefault(b => b.Path == relative && b.Text == text);
                    if (batch is null)
                    {
                        await Task.Delay(100);
                    }
                }
                await JsonStore.WriteAsync(Path.Combine(output, Path.GetFileName(relative) + ".diagnostics.json"), batch);
                Check(batch is not null && batch.Items.All(d => d.Severity != 1), "clangd uses CMake headers and defines: " + relative);
            }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            checks,
            project,
            hardwareOpened = false
        });
    }
}

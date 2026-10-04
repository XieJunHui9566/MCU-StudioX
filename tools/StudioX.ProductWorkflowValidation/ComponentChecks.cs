namespace StudioX.ProductWorkflowValidation;

using StudioX.Application.Components;
using StudioX.Engine;
using StudioX.Foundation;

internal static class ComponentChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var project = Path.Combine(root, "component-project");
        var manifest = new ProjectManifest(1, "fixture", "", "", "", "STM32F407ZGT6", "", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1", ProjectKind.CubeMx, new("fixture.ioc", "fixture.cmake", "Debug", "Debug"));
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest);
        var cmake = Path.Combine(project, "CMakeLists.txt");
        const string original = "cmake_minimum_required(VERSION 3.20)\nproject(fixture C)\nadd_executable(firmware main.c)\n";
        await File.WriteAllTextAsync(cmake, original);
        var active = false;
        var builds = new BuildService(new ToolsetCatalog(Path.Combine(root, "no-tools")));
        var service = new ComponentService(() => active, builds);
        var one = await service.PreviewAsync(FixtureArchives.Component(root, "1.0.0"));
        check(one.Manifest.License == "NOASSERTION" && one.Bytes > 0, "component preview retains version, source, license and unfolded size");
        await service.InstallAsync(project, one, "firmware");
        var installed = (await service.ReadAsync(project))!;
        check(installed.Components.Single().Manifest.Version == "1.0.0" && (await File.ReadAllTextAsync(cmake)).StartsWith(original), "component install adds controlled include and preserves original CMake");
        check((await File.ReadAllTextAsync(Path.Combine(project, "studiox-components.cmake"))).Contains("target_sources(firmware") && installed.GeneratedHashes.ContainsKey("studiox-components.cmake"), "native source and header contribution is generated and content locked");
        var two = await service.PreviewAsync(FixtureArchives.Component(root, "1.0.1"));
        await service.InstallAsync(project, two, "firmware");
        check(Directory.Exists(Path.Combine(project, "studiox-components/fixture.byte/1.0.0")) && (await service.ReadAsync(project))!.Components.Single().Manifest.Version == "1.0.1", "component update keeps previous immutable version");
        await service.RollbackAsync(project);
        check((await service.ReadAsync(project))!.Components.Single().Manifest.Version == "1.0.0" && (await File.ReadAllTextAsync(Path.Combine(project, "studiox-components.cmake"))).Contains("fixture.byte/1.0.0"), "component rollback restores prior build inputs");
        var generated = Path.Combine(project, "studiox-components.cmake");
        var saved = await File.ReadAllTextAsync(generated);
        await File.AppendAllTextAsync(generated, "# user modification\n");
        await Reject(() => service.InstallAsync(project, two, "firmware"), "COMPONENT_EDITED", "externally edited generated CMake is never overwritten", check);
        check((await File.ReadAllTextAsync(generated)).EndsWith("# user modification\n"), "external edit survives rejected component update");
        await File.WriteAllTextAsync(generated, saved);
        active = true;
        await Reject(() => service.InstallAsync(project, two, "firmware"), "COMPONENT_BUSY", "active debug blocks component mutation", check);
        active = false;
        using (builds.AcquireMaintenance())
        {
            await Reject(() => service.InstallAsync(project, two, "firmware"), "BUILD_BUSY", "build and component maintenance share exclusive lease", check);
        }
        await Reject(() => service.PreviewAsync(FixtureArchives.Component(root, "1.0.0", corrupt: true)), "COMPONENT_HASH", "corrupt component bytes rejected before project edits", check);
        await Reject(() => service.PreviewAsync(FixtureArchives.Component(root, "1.0.0", extra: "extra.txt")), "COMPONENT_INDEX", "unindexed component file rejected", check);
        await Reject(() => service.PreviewAsync(FixtureArchives.Component(root, "1.0.0", extra: "../escape.c")), "COMPONENT_PATH", "component traversal rejected", check);
        await Reject(() => service.InstallAsync(project, two, "firmware);execute_process("), "COMPONENT_TARGET", "component CMake target injection rejected", check);
        var sdkProject = Path.Combine(root, "idf-component-project");
        await JsonStore.WriteAsync(Path.Combine(sdkProject, ".studiox/project.json"), manifest with
        {
            Kind = ProjectKind.Pack,
            CubeMx = null,
            ToolsetId = "espressif.idf",
            ToolsetVersion = "5.5.4",
            CompilerId = "esp-idf",
            DeviceId = "ESP32-S3",
            Espressif = new("esp-idf", "esp32s3", "5.5.4")
        });
        await service.InstallAsync(sdkProject, one, "firmware");
        var registered = await File.ReadAllTextAsync(Path.Combine(sdkProject, "components/studiox_fixture_byte/CMakeLists.txt"));
        check(registered.Contains("idf_component_register") && registered.Contains("../../studiox-components/fixture.byte/1.0.0") && registered.Contains("MINIMAL_BUILD"), "IDF component registration uses pinned sources and explicit minimal build guidance");
        var otherSdk = Path.Combine(root, "esp8266-component-project");
        await JsonStore.WriteAsync(Path.Combine(otherSdk, ".studiox/project.json"), manifest with
        {
            Kind = ProjectKind.Pack,
            CubeMx = null,
            ToolsetId = "espressif.esp8266-rtos",
            ToolsetVersion = "3.4.0",
            CompilerId = "esp8266-rtos",
            DeviceId = "ESP8266",
            Espressif = new("esp8266-rtos-sdk", "esp8266", "3.4.0")
        });
        await Reject(() => service.InstallAsync(otherSdk, one, "firmware"), "COMPONENT_FRAMEWORK", "other SDK is not silently treated as native CMake", check);
    }
    private static async Task Reject(Func<Task> action, string code, string message, Action<bool, string> check)
    {
        try
        {
            await action();
            check(false, message);
        }
        catch (StudioXException error) { check(error.Code == code, message + " (" + error.Code + ")"); }
    }
}

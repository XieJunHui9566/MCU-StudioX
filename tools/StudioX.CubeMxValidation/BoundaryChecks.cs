using StudioX.Application;
using StudioX.Foundation;

internal static class BoundaryChecks
{
    public static async Task<int> RunAsync(string runtime, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new check directory.");
        Directory.CreateDirectory(Path.Combine(root, "cmake/stm32cubemx"));
        await File.WriteAllTextAsync(Path.Combine(root, "Boundary.ioc"), "Mcu.CPN=STM32F103C8T6\nProjectManager.ProjectName=Boundary\n");
        await File.WriteAllTextAsync(Path.Combine(root, "cmake/stm32cubemx/CMakeLists.txt"), "# Isolated import fixture\n");
        await File.WriteAllTextAsync(Path.Combine(root, "cmake/gcc-arm-none-eabi.cmake"), """
            set(CMAKE_SYSTEM_NAME Generic)
            set(CMAKE_SYSTEM_PROCESSOR arm)
            set(CMAKE_C_COMPILER arm-none-eabi-gcc)
            set(CMAKE_ASM_COMPILER arm-none-eabi-gcc)
            set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
            set(CMAKE_EXECUTABLE_SUFFIX_C .elf)
            set(CMAKE_C_FLAGS "-mcpu=cortex-m3 -mthumb")
            """);
        var cmake = """
            cmake_minimum_required(VERSION 3.24)
            project(Boundary LANGUAGES C ASM)
            add_executable(custom_target main.c)
            target_link_options(custom_target PRIVATE -nostdlib -Wl,-e,main)
            # Existing user-produced image must not be overwritten by the IDE.
            file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/custom_target.bin" "CUSTOM_IMAGE")
            """;
        await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), cmake);
        await File.WriteAllTextAsync(Path.Combine(root, "main.c"), "int main(void) { for (;;) {} }\n");
        await using var services = new WorkbenchService(runtime, Path.Combine(root, "user-data"));
        async Task Reject(string code, Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("Expected " + code); }
            catch (StudioXException ex) when (ex.Code == code) { }
        }
        await Reject("CUBEMX_LAYOUT", async () => { await services.CubeMx.InspectAsync(Path.Combine(root, "missing")); });
        await Reject("CUBEMX_PATH", async () => { await services.CubeMx.InspectAsync(Path.Combine(root, "中文")); });
        await Reject("CUBEMX_PRESET", async () => { await services.CubeMx.ImportAsync(root, "Nonexistent"); });
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await services.CubeMx.ImportAsync(root, token: cancel.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        if (File.Exists(Path.Combine(root, ".studiox/project.json"))) throw new InvalidOperationException("Rejected import left metadata.");
        var inspection = await services.CubeMx.InspectAsync(root);
        if (inspection.ConfigurePresets.Count != 0) throw new InvalidOperationException("Invented presets.");
        await services.CubeMx.ImportAsync(root, buildType: "Release");
        var configure = await services.Builds.ConfigureAsync(root);
        if (!configure.Success || File.Exists(Path.Combine(root, ".build/custom_target.elf"))) throw new InvalidOperationException("Configure compiled source or failed: " + configure.Log);
        var result = await services.Builds.BuildAsync(root);
        if (!result.Success) throw new InvalidOperationException(result.Log);
        if (await File.ReadAllTextAsync(Path.Combine(root, ".build/custom_target.bin")) != "CUSTOM_IMAGE" ||
            !result.Artifacts.Any(path => path.EndsWith("studiox-artifacts\\custom_target.hex", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Custom output overwritten or missing generated HEX.");
        if (!(await File.ReadAllTextAsync(Path.Combine(root, ".build/CMakeCache.txt"))).Contains("CMAKE_BUILD_TYPE:STRING=Release", StringComparison.Ordinal))
            throw new InvalidOperationException("Manual build type ignored.");
        await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), cmake + "\nmessage(FATAL_ERROR STUDIOX_EXPECTED_CUBEMX_ERROR)\n");
        var failed = await services.Builds.BuildAsync(root);
        if (failed.Success || failed.ExitCode == 0 || !failed.Log.Contains("STUDIOX_EXPECTED_CUBEMX_ERROR", StringComparison.Ordinal) || !failed.Log.Contains("编译失败，退出代码", StringComparison.Ordinal))
            throw new InvalidOperationException("Configure error lost diagnostics/exit code.");
        var cacheIdentity = Path.Combine(root, ".build/studiox-runtime.json");
        if (File.Exists(cacheIdentity)) throw new InvalidOperationException("Failed configure left a reusable cache identity.");
        await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), cmake);
        var recovered = await services.Builds.BuildAsync(root);
        if (!recovered.Success || !File.Exists(cacheIdentity)) throw new InvalidOperationException("Failed configure could not recover: " + recovered.Log);
        // 模拟升级前已被 CMake 原生探测污染的缓存；新版必须自动弃用旧配置标识。
        var legacyIdentity = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(cacheIdentity))!.AsObject();
        legacyIdentity.Remove("configurationVersion");
        await File.WriteAllTextAsync(cacheIdentity, legacyIdentity.ToJsonString());
        foreach (var system in Directory.EnumerateFiles(Path.Combine(root, ".build/CMakeFiles"), "CMakeSystem.cmake", SearchOption.AllDirectories))
            await File.WriteAllTextAsync(system, "set(CMAKE_SYSTEM_NAME Windows)\nset(CMAKE_SYSTEM_LOADED 1)\nset(CMAKE_CROSSCOMPILING FALSE)\n");
        await File.AppendAllTextAsync(Path.Combine(root, ".build/CMakeCache.txt"), "\nCMAKE_C_STANDARD_LIBRARIES:STRING=-lkernel32 -luser32\n");
        var migrated = await services.Builds.BuildAsync(root);
        if (!migrated.Success || !migrated.Log.Contains("重建 CMake 缓存", StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy native cache was not repaired: " + migrated.Log);
        var incremental = await services.Builds.BuildAsync(root);
        if (!incremental.Success || !incremental.Log.Contains("ninja: no work to do", StringComparison.Ordinal) || incremental.Log.Contains("compiler identification", StringComparison.Ordinal))
            throw new InvalidOperationException("Migration disabled incremental builds: " + incremental.Log);
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: invalid layout/preset/Unicode path rejection; canceled import leaves no metadata; no-presets Release; configure-only; actual custom target; preserve custom BIN; missing HEX generated separately; failed CMake retains raw diagnostic and exit code; failed cache recovery; legacy Windows cache migration; incremental build preserved.\n");
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(root, "result.txt")));
        return 0;
    }
}

namespace StudioX.Application.CodeIntelligence;

using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;

/// <summary>固定板型的 C++ 编辑配置；sketch 自动原型和目标代码生成以官方 CLI 为准。</summary>
internal sealed record ArduinoAnalysisProfile(string[] Flags, string HeaderRoot, string LibrariesRoot)
{
    public static async Task<ArduinoAnalysisProfile> ReadAsync(string runtime, string data, string projectRoot, ProjectManifest project, CancellationToken token)
    {
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"), data);
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        if (tools.Manifest.Purpose != "arduino")
        {
            throw new StudioXException("ARDUINO_RUNTIME", "Arduino 分析需要工程锁定的官方工具组件。");
        }
        var settings = project.Arduino!;
        var seed = tools.ResourceDirectory("arduino-seed");
        if (settings.Platform.Id == "esp32")
        {
            var board = ArduinoBoard.Require(settings.BoardId);
            var command = await new ArduinoBuildService(catalog, data).CompilationCommandAsync(projectRoot, token);
            var headers = PathBoundary.Resolve(data, "arduino/r/" + tools.Fingerprint[..20] + "/data/packages");
            var xtensa = EspressifAnalysisProfile.IsXtensa(board.Target!);
            var triple = xtensa ? "xtensa-esp-elf" : "riscv32-esp-elf";
            var prefix = PathBoundary.Resolve(headers, "esp32/tools/" + (xtensa ? "esp-x32" : "esp-rv32") + "/2601");
            var options = new List<string>
            {
                xtensa ? "--target=i386-unknown-elf" : "--target=riscv32-unknown-elf", "-ffreestanding", "-ferror-limit=0",
                "-include", PathBoundary.Resolve(headers, "esp32/hardware/esp32/3.3.8/cores/esp32/Arduino.h")
            };
            if (xtensa)
            {
                options.Add("-D__XTENSA__=1");
            }
            foreach (var include in new[] { triple + "/include", triple + "/include/c++/14.2.0", triple + "/include/c++/14.2.0/" + triple, "lib/gcc/" + triple + "/14.2.0/include" })
            {
                var path = PathBoundary.Resolve(prefix, include);
                if (Directory.Exists(path))
                {
                    options.AddRange(["-isystem", path]);
                }
            }
            var translator = new EspressifAnalysisProfile(board.Target!, tools.RootDirectory, options.ToArray(), "Arduino ESP32", tools);
            var translated = translator.Translate(command.Arguments, command.Directory, command.File, "clang++");
            return new(translated[1..^1], headers, new ArduinoRuntime(catalog, data).LibraryRoot(settings));
        }
        var core = PathBoundary.Resolve(seed, "packages/arduino/hardware/avr/" + ArduinoProjectSettings.CoreVersion);
        var gcc = PathBoundary.Resolve(seed, "packages/arduino/tools/avr-gcc/7.3.0-atmel3.6.1-arduino7");
        var libraries = new ArduinoRuntime(catalog, data).LibraryRoot(settings);
        var flags = new List<string>
        {
            "--target=avr", "-mmcu=atmega328p", "-std=gnu++11", "-DF_CPU=16000000L", "-DARDUINO=10607", "-DARDUINO_ARCH_AVR",
            settings.BoardId == "uno" ? "-DARDUINO_AVR_UNO" : "-DARDUINO_AVR_NANO", "-ferror-limit=0",
            "-isystem", PathBoundary.Resolve(gcc, "avr/include"),
            "-isystem", PathBoundary.Resolve(gcc, "lib/gcc/avr/7.3.0/include"),
            "-I" + PathBoundary.Resolve(core, "cores/arduino"),
            "-I" + PathBoundary.Resolve(core, settings.BoardId == "uno" ? "variants/standard" : "variants/eightanaloginputs"),
            "-include", PathBoundary.Resolve(core, "cores/arduino/Arduino.h")
        };
        foreach (var collection in new[] { PathBoundary.Resolve(core, "libraries"), PathBoundary.Resolve(libraries, "libraries") })
        {
            if (!Directory.Exists(collection))
            {
                continue;
            }
            foreach (var library in Directory.EnumerateDirectories(collection))
            {
                var checkedLibrary = PathBoundary.Resolve(collection, Path.GetFileName(library));
                flags.Add("-I" + checkedLibrary);
                if (Directory.Exists(PathBoundary.Resolve(checkedLibrary, "src")))
                {
                    flags.Add("-I" + PathBoundary.Resolve(checkedLibrary, "src"));
                }
            }
        }
        return new(flags.ToArray(), tools.RootDirectory, libraries);
    }
}

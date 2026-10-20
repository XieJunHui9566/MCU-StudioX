namespace StudioX.Engine.Arduino;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>创建独立 Arduino sketch，导入时复制源码，始终保留原工程。</summary>
public sealed class ArduinoProjectService(Func<string, CancellationToken, Task>? initializeRepository = null)
{
    public Task<ProjectManifest> CreateAsync(string name, string boardId, string destination,
        string? importDirectory = null, CancellationToken token = default) =>
        CreateCoreAsync(name, boardId, destination, importDirectory, null, null, token);

    public async Task<ProjectManifest> CreateFromPackAsync(InstalledPack pack, string deviceId, string boardId,
        string name, string destination, CancellationToken token = default)
    {
        pack = await PackRepository.VerifyAsync(pack, token);
        var device = pack.Manifest.Devices.SingleOrDefault(item => item.Id == deviceId)
            ?? throw new StudioXException("PROJECT_DEVICE", "请选择明确的芯片型号。");
        if (!ArduinoBoard.ForDevice(device).Any(board => board.Id == boardId))
        {
            throw new StudioXException("ARDUINO_DEVICE", "Arduino 开发板配置与所选芯片不匹配。");
        }
        return await CreateCoreAsync(name, boardId, destination, null, pack, deviceId, token);
    }

    private async Task<ProjectManifest> CreateCoreAsync(string name, string boardId, string destination,
        string? importDirectory, InstalledPack? pack, string? deviceId, CancellationToken token)
    {
        if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]{0,62}\z"))
        {
            throw new StudioXException("ARDUINO_NAME", "工程名请使用字母或下划线开头的英文、数字和下划线，最多 63 字符。");
        }
        var board = ArduinoBoard.Require(boardId);
        var platform = board.Platform;
        var target = Path.GetFullPath(destination);
        // 正则允许的 COM3 / NUL 等名称仍是 Windows 设备别名，创建目录和写模板前必须先拒绝。
        _ = PathBoundary.Resolve(target, name + ".ino");
        if (Path.GetFileName(target) != name || Directory.Exists(target) || File.Exists(target))
        {
            throw new StudioXException("PROJECT_EXISTS", "请选择不存在的工程目录，目录名必须与 sketch 名称一致。");
        }
        var project = new ProjectManifest(1, name, pack?.Manifest.Id ?? platform.ToolsetId,
            pack?.Manifest.Version ?? platform.CoreVersion, pack?.ContentHash ?? "", deviceId ?? board.Fqbn,
            "arduino-sketch", platform.ToolsetId, platform.CoreVersion,
            platform.CompilerId, Kind: ProjectKind.Arduino, EntryFile: name + ".ino",
            Arduino: new(boardId, []));
        var parent = Path.GetDirectoryName(target)!;
        if (importDirectory is not null && target.StartsWith(Path.GetFullPath(importDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ARDUINO_IMPORT_TARGET", "导入副本请选择原 sketch 之外的位置，避免把正在创建的副本递归导入。");
        }
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-arduino-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            if (importDirectory is not null)
            {
                var source = Path.GetFullPath(importDirectory);
                var originalName = Path.GetFileName(source);
                if (!File.Exists(PathBoundary.Resolve(source, originalName + ".ino")))
                {
                    throw new StudioXException("ARDUINO_SKETCH", "导入目录必须包含与目录同名的 .ino 文件。");
                }
                foreach (var relative in ArduinoSourceSnapshot.Files(source))
                {
                    token.ThrowIfCancellationRequested();
                    // 外部 profile 可能隐式下载核心和改选依赖；保留原文件，但不让 CLI 自动使用它。
                    var copied = relative.Equals("sketch.yaml", StringComparison.OrdinalIgnoreCase) ? ".studiox/import/sketch.yaml"
                        : relative.Equals(originalName + ".ino", StringComparison.OrdinalIgnoreCase) ? name + ".ino" : relative;
                    var output = PathBoundary.Resolve(staging, copied);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(PathBoundary.Resolve(source, relative), output);
                }
            }
            else
            {
                await File.WriteAllTextAsync(PathBoundary.Resolve(staging, name + ".ino"), """
                    #include <Arduino.h>

                    void setup()
                    {
                        pinMode(LED_BUILTIN, OUTPUT);
                        Serial.begin(115200);
                    }

                    void loop()
                    {
                        // 无符号差值允许 millis() 回绕，不阻塞后续传感器与按键任务。
                        static unsigned long last_toggle_ms = 0;
                        static bool led_on = false;
                        const unsigned long now_ms = millis();
                        if (now_ms - last_toggle_ms >= 500UL)
                        {
                            last_toggle_ms = now_ms;
                            led_on = !led_on;
                            digitalWrite(LED_BUILTIN, led_on ? HIGH : LOW);
                            Serial.println(led_on ? 1 : 0);
                        }
                    }
                    """ + "\n", token);
                if (board.PlatformId == "esp32")
                {
                    // 通用 ESP 模组没有统一 LED 引脚；模板只使用串口，避免猜测开发板接线。
                    await File.WriteAllTextAsync(PathBoundary.Resolve(staging, name + ".ino"), """
                        #include <Arduino.h>

                        void setup()
                        {
                            Serial.begin(115200);
                        }

                        void loop()
                        {
                            static unsigned long last_report_ms = 0;
                            const unsigned long now_ms = millis();
                            if (now_ms - last_report_ms >= 1000UL)
                            {
                                last_report_ms = now_ms;
                                Serial.println("Hello from Arduino on ESP32");
                            }
                            delay(1);
                        }
                        """ + "\n", token);
                }
            }
            await JsonStore.WriteAsync(Path.Combine(staging, ".studiox", "project.json"), project, token);
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), ".build/\n", token);
            if (initializeRepository is not null)
            {
                await initializeRepository(staging, token);
            }
            token.ThrowIfCancellationRequested();
            await ProjectService.PublishDirectoryAsync(staging, target, token);
            return project;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
            throw;
        }
    }
}

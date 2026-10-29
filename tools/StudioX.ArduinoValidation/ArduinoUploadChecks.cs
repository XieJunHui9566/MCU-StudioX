using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application.Arduino;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;

/// <summary>真实 CLI 配合隔离的上传替身验证流程；不连接或写入任何物理串口。</summary>
internal static class ArduinoUploadChecks
{
    public static async Task RunUploaderAsync(string port, string hex, string modePath)
    {
        await File.WriteAllTextAsync(modePath + ".called", JsonSerializer.Serialize(new
        {
            port,
            hex
        }));
        var mode = (await File.ReadAllTextAsync(modePath)).Trim();
        if (mode == "wait")
        {
            Console.WriteLine("fixture uploader waiting");
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        if (mode == "failure")
        {
            Console.Error.WriteLine("fixture uploader: stk500_getsync(): not in sync");
            Environment.ExitCode = 1;
            return;
        }
        if (mode != "silent")
        {
            if (!File.Exists(hex) || port != "COM9876" || !hex.EndsWith(".ino.hex", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Uploader received a different port or image.");
            }
            Console.Error.WriteLine("avrdude: 1234 bytes of flash written");
            Console.Error.WriteLine("avrdude: 1234 bytes of flash verified");
        }
    }

    public static async Task RunAsync(string evidence, string runtime)
    {
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        async Task Reject(Func<Task> action, string code, string message)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Expected " + code);
            }
            catch (StudioXException error) when (error.Code == code) { Check(true, message); }
        }
        var mode = Path.Combine(evidence, "uploader-mode.txt");
        var fixtureRoot = Path.Combine(evidence, "runtime/toolsets/arduino.avr/1.8.8");
        var component = Path.Combine(runtime, "toolsets/arduino.avr/1.8.8");
        // 复制并重新索引测试工具组件；生产组件、用户工程与现有内容锁都不改写。
        foreach (var source in Directory.EnumerateFiles(component, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(fixtureRoot, Path.GetRelativePath(component, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        var localPath = Path.Combine(fixtureRoot, "seed/packages/arduino/hardware/avr/1.8.8/platform.local.txt");
        await File.WriteAllTextAsync(localPath,
            $"tools.avrdude.upload.pattern=\"{Environment.ProcessPath}\" --uploader-fixture \"{{serial.port}}\" \"{{build.path}}/{{build.project_name}}.hex\" \"{mode}\"\n");
        var manifestPath = Path.Combine(fixtureRoot, "toolset.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["sha256"]!["seed/packages/arduino/hardware/avr/1.8.8/platform.local.txt"] =
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(localPath)));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var builds = new BuildService(new ToolsetCatalog(Path.Combine(evidence, "runtime/toolsets")),
            userDataDirectory: Path.Combine(evidence, "user-data"));
        await using var devices = new DeviceHub();
        var available = new[] { "COM9876" };
        var service = new ArduinoService(builds, devices, listPorts: () => Task.FromResult(available));
        var root = Path.Combine(evidence, "中文 parent with spaces/UploadSketch");
        var project = await service.CreateAsync("UploadSketch", "uno", root);
        Check((await service.LoadUploadSettingsAsync(root)).Port == "", "new projects do not guess an upload port");
        await service.SaveUploadSettingsAsync(root, " com9876 ");
        Check((await service.LoadUploadSettingsAsync(root)).Port == "COM9876", "explicit port is normalized and persists per project");
        Check((await builds.BuildAsync(root)).Success, "real AVR compilation succeeds before upload workflow checks");
        var preview = await service.PreviewUploadAsync(root);
        var sourceHash = await ArduinoSourceSnapshot.HashAsync(root, true, CancellationToken.None);
        await service.SaveUploadSettingsAsync(root, "COM9876");
        Check((await service.PreviewUploadAsync(root)).ApprovalId == preview.ApprovalId &&
            await ArduinoSourceSnapshot.HashAsync(root, true, CancellationToken.None) == sourceHash,
            "port settings do not invalidate compiled source or image approval");
        await File.WriteAllTextAsync(mode, "success");
        var report = await service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId);
        var log = await File.ReadAllTextAsync(report.LogPath);
        var invocation = JsonNode.Parse(await File.ReadAllTextAsync(mode + ".called"))!;
        Check(report.Success && report.Verified && log.Contains("bytes of flash verified") && log.Contains(preview.Sha256),
            "real CLI invokes uploader and retains verification, image identity and conclusion");
        Check(invocation["port"]!.GetValue<string>() == "COM9876" &&
            Path.GetDirectoryName(invocation["hex"]!.GetValue<string>()) != Path.GetDirectoryName(preview.HexPath),
            "uploader receives only the selected port and independent confirmed HEX snapshot");
        await File.WriteAllTextAsync(mode, "silent");
        var silent = await service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId);
        Check(!silent.Success && silent.Result.Success && !silent.Verified,
            "CLI exit zero with only New upload port cannot claim flash verification");
        await File.WriteAllTextAsync(mode, "failure");
        var failed = await service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId);
        Check(!failed.Success && failed.Result.ExitCode != 0 &&
            (await File.ReadAllTextAsync(failed.LogPath)).Contains("stk500_getsync"),
            "uploader failure propagates exit code and original bootloader diagnostic");
        available = [];
        await Reject(async () => { await service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId); },
            "ARDUINO_PORT_UNAVAILABLE", "disconnected saved port is rejected without choosing another device");
        available = ["COM9876"];
        using (await devices.ReserveAsync("serial:COM9876"))
        {
            await Reject(async () => { await service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId); },
                "DEVICE_OWNED", "upload respects the serial session owner");
        }
        File.Delete(mode + ".called");
        await File.WriteAllTextAsync(mode, "wait");
        using (var cancellation = new CancellationTokenSource())
        {
            var uploading = service.UploadApprovedAsync(root, "COM9876", preview.ApprovalId, token: cancellation.Token);
            for (var attempt = 0; attempt < 300 && !File.Exists(mode + ".called") && !uploading.IsCompleted; attempt++)
            {
                await Task.Delay(50);
            }
            Check(File.Exists(mode + ".called"), "cancellation fixture reaches actual uploader invocation");
            cancellation.Cancel();
            try
            {
                await uploading;
                throw new InvalidOperationException("Upload cancellation ignored.");
            }
            catch (OperationCanceledException) { Check(true, "upload cancellation terminates CLI and uploader tree"); }
        }
        var cancellationLogs = Directory.GetFiles(Path.Combine(root, ".build/arduino-upload"), "upload.log", SearchOption.AllDirectories);
        Check(cancellationLogs.Any(path => File.ReadAllText(path).Contains("OperationCanceledException")),
            "cancelled upload retains raw diagnostic and unfinished result on disk");
        using (await devices.ReserveAsync("serial:COM9876"))
        {
            Check(true, "upload cancellation releases port ownership");
        }
        var nano = await service.ChangeBoardAsync(root, project, "nano-old");
        Check(nano.Arduino!.BoardId == "nano-old" && nano.DeviceId == ArduinoBoard.Require("nano-old").Fqbn,
            "existing AVR project can explicitly select Nano old bootloader");
        await Reject(async () => { await service.PreviewUploadAsync(root); }, "ARDUINO_BUILD_REQUIRED",
            "board change revokes previous firmware approval");
        await Reject(async () => { await service.ChangeBoardAsync(root, project, "uno"); }, "ARDUINO_PROJECT_CHANGED",
            "stale manager configuration cannot overwrite a newer board selection");
        await Reject(async () => { await service.ChangeBoardAsync(root, nano, "esp32"); }, "ARDUINO_DEVICE",
            "board configuration cannot change the underlying chip family");
        Check((await builds.BuildAsync(root)).Success && (await service.PreviewUploadAsync(root)).Board.Id == "nano-old",
            "changed board configuration compiles and produces a matching upload preview");
        await File.WriteAllTextAsync(Path.Combine(evidence, "upload-validation.json"), JsonSerializer.Serialize(new
        {
            passed = true,
            hardware = false,
            actualCli = ArduinoProjectSettings.CliVersion,
            uploader = "isolated fixture; no physical device access",
            project = root,
            checks
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

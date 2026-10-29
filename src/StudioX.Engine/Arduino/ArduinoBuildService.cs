namespace StudioX.Engine.Arduino;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>执行固定 Arduino 构建、库事务与上传预检；上传与编译始终是两个显式操作。</summary>
public sealed class ArduinoBuildService(ToolsetCatalog catalog, string dataDirectory)
{
    private readonly ArduinoRuntime runtime = new(catalog, dataDirectory);

    public async Task<ArduinoCompilationCommand> CompilationCommandAsync(string root, CancellationToken token = default)
    {
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token);
        await RequireLibrariesAsync(execution, project.Arduino!, token);
        var build = PathBoundary.Resolve(dataDirectory, "arduino/analysis/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(build);
        var nativeBuild = project.Arduino!.Platform.Id == "esp32" ? EspressifNativePath.For(build) : build;
        var result = await execution.RunAsync(["compile", "--fqbn", ArduinoBoard.Require(project.Arduino!.BoardId).Fqbn,
            "--only-compilation-database", "--build-path", nativeBuild, Path.GetFullPath(root)], root, null, token);
        RequireSuccess(result);
        await RequireSameProjectAsync(root, project, token);
        using var database = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(build, "compile_commands.json"), token));
        var command = database.RootElement.EnumerateArray().First(item => item.GetProperty("file").GetString()!.EndsWith(".ino.cpp", StringComparison.Ordinal));
        return new(command.GetProperty("directory").GetString()!, command.GetProperty("file").GetString()!,
            command.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    public async Task<BuildReport> BuildAsync(string root, IProgress<string>? output = null, CancellationToken token = default)
    {
        var receiptPath = PathBoundary.Resolve(root, ArduinoBuildReceipt.RelativePath);
        File.Delete(receiptPath);
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token, output);
        await RequireLibrariesAsync(execution, project.Arduino!, token);
        await RequireSameProjectAsync(root, project, token);
        var dependency = await CheckDependenciesAsync(root, execution, token);
        var source = await ArduinoSourceSnapshot.HashAsync(root, true, token);
        var run = ".build/arduino/" + Guid.NewGuid().ToString("N");
        var build = PathBoundary.Resolve(root, run + "/build");
        var artifacts = PathBoundary.Resolve(root, run + "/output");
        var board = ArduinoBoard.Require(project.Arduino!.BoardId);
        using var workspace = new ArduinoBuildWorkspace(dataDirectory, build, board.PlatformId == "esp32");
        var arguments = new List<string> { "compile", "--fqbn", board.Fqbn, "--warnings", "all", "--jobs", "4",
            "--build-path", workspace.NativeDirectory, "--output-dir", artifacts };
        if (board.PlatformId == "esp32")
        {
            // 固定 ESP32 核心的 savehex hooks 使用 sketch_path 另行导出文件；将这些副作用留在私有构建树。
            // CLI 的实际草图输入仍为原工程，不能通过排除工程内任意 build 目录来绕过源码一致性检查。
            arguments.AddRange(["--build-property", "sketch_path=" + workspace.NativeDirectory]);
        }
        arguments.Add(Path.GetFullPath(root));
        var result = await execution.RunAsync(arguments, root, output, token);
        var log = result.StandardOutput + "\n" + result.StandardError;
        var logPath = PathBoundary.Resolve(root, run + "/build.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, log, token);
        await workspace.PublishAsync(token);
        var hexRelative = run + "/output/" + project.Name + board.FirmwareSuffix;
        var hex = PathBoundary.Resolve(root, hexRelative);
        var success = result.ExitCode == 0 && !result.TimedOut && File.Exists(hex);
        if (success)
        {
            if (project != await RequireAsync(root, token) || source != await ArduinoSourceSnapshot.HashAsync(root, true, token) ||
                dependency.LibrariesSha256 != await LibrariesHashAsync(execution, token))
            {
                throw new StudioXException("ARDUINO_BUILD_CHANGED", "编译期间源码、工程或库发生变化；产物未获得上传凭据，请重新编译。");
            }
            Dictionary<string, string>? uploadFiles = null;
            if (board.PlatformId == "esp32")
            {
                uploadFiles = new(StringComparer.Ordinal);
                foreach (var image in Directory.GetFiles(artifacts, "*.bin").Order(StringComparer.Ordinal))
                {
                    uploadFiles.Add(Path.GetFileName(image), await FileHashAsync(image, token));
                }
                if (!RequiredEspImages(project.Name).All(uploadFiles.ContainsKey))
                {
                    throw new StudioXException("ARDUINO_ARTIFACT", "ESP32 构建缺少主程序、分区表或 bootloader 镜像，不能上传。");
                }
            }
            await JsonStore.WriteAsync(receiptPath, new ArduinoBuildReceipt(1, source, execution.Tools.Fingerprint,
                dependency.LibrariesSha256, hexRelative, await FileHashAsync(hex, token), uploadFiles), token);
        }
        return new(success, log, success ? Directory.GetFiles(artifacts).Order(StringComparer.Ordinal).ToArray() : [],
            logPath, result.ExitCode, result.TimedOut);
    }

    public async Task<ArduinoUploadPreview> PreviewAsync(string root, CancellationToken token = default)
    {
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token);
        await RequireLibrariesAsync(execution, project.Arduino!, token);
        return await ValidateReceiptAsync(root, project, execution, token);
    }

    public static string ValidatePort(string port)
    {
        port = port.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(port, @"\ACOM[1-9][0-9]{0,3}\z"))
        {
            throw new StudioXException("ARDUINO_PORT", "请明确填写板卡的 USB 串口，例如 COM3；不会自动选择端口。");
        }
        return port;
    }

    public async Task<ProjectManifest> ChangeBoardAsync(string root, ProjectManifest expected, string boardId,
        CancellationToken token = default)
    {
        var project = await RequireAsync(root, token);
        if (project != expected)
        {
            throw new StudioXException("ARDUINO_PROJECT_CHANGED", "工程配置已变化，请重新打开 Arduino 工程与库管理。");
        }
        var previous = ArduinoBoard.Require(project.Arduino!.BoardId);
        var board = ArduinoBoard.Require(boardId);
        if (board.DeviceId != previous.DeviceId || board.PlatformId != previous.PlatformId || board.Target != previous.Target)
        {
            throw new StudioXException("ARDUINO_DEVICE", "只能选择与当前工程芯片匹配的开发板配置；更换芯片请新建工程。");
        }
        if (board == previous)
        {
            return project;
        }
        var updated = project with
        {
            DeviceId = project.DeviceId == previous.Fqbn ? board.Fqbn : project.DeviceId,
            Arduino = project.Arduino with
            {
                BoardId = boardId
            }
        };
        File.Delete(PathBoundary.Resolve(root, ArduinoBuildReceipt.RelativePath));
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ".studiox/project.json"), updated, token);
        return updated;
    }

    public async Task<ArduinoUploadReport> UploadApprovedAsync(string root, string port, string approvedIdentity,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        port = ValidatePort(port);
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token);
        await RequireLibrariesAsync(execution, project.Arduino!, token);
        var preview = await ValidateReceiptAsync(root, project, execution, token);
        if (!preview.ApprovalId.Equals(approvedIdentity, StringComparison.Ordinal))
        {
            throw new StudioXException("ARDUINO_UPLOAD_CHANGED", "确认后的固件发生变化，请重新检查和确认。");
        }
        var runId = Guid.NewGuid().ToString("N");
        var logPath = PathBoundary.Resolve(root, ".build/arduino-upload/" + runId + "/upload.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new ArduinoUploadLog(logPath, output);
        log.Report($"Arduino 上传 · {DateTimeOffset.Now:O}（宿主时间）\n板型：{preview.Board.Fqbn}\n端口：{port}\n固件：{Path.GetFileName(preview.HexPath)}\nSHA-256：{preview.Sha256}\n上传日志：{logPath}\n");
        var snapshot = PathBoundary.Resolve(dataDirectory, "arduino/uploads/" + runId + "/" + Path.GetFileName(preview.HexPath));
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        var arguments = new List<string> { "upload", "--fqbn", preview.Board.Fqbn, "--port", port,
            "--verify", "--verbose" };
        if (preview.Board.PlatformId == "esp32")
        {
            var receipt = await JsonStore.ReadAsync<ArduinoBuildReceipt>(PathBoundary.Resolve(root, ArduinoBuildReceipt.RelativePath), token);
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt)))) != approvedIdentity)
            {
                throw new StudioXException("ARDUINO_UPLOAD_CHANGED", "ESP32 构建记录在确认后发生变化。");
            }
            foreach (var (name, hash) in receipt.UploadFiles!)
            {
                var destination = PathBoundary.Resolve(Path.GetDirectoryName(snapshot)!, name);
                File.Copy(PathBoundary.Resolve(Path.GetDirectoryName(preview.HexPath)!, name), destination);
                if (await FileHashAsync(destination, token) != hash)
                {
                    throw new StudioXException("ARDUINO_UPLOAD_CHANGED", "ESP32 多镜像快照与确认内容不一致。");
                }
            }
            // ESP32 上传由锁定核心的官方 recipe 选择地址；不把合并镜像或 AVR HEX 当作主程序。
            arguments.AddRange(["--input-dir", Path.GetDirectoryName(snapshot)!]);
        }
        else
        {
            File.Copy(preview.HexPath, snapshot);
            if (await FileHashAsync(snapshot, token) != preview.Sha256)
            {
                throw new StudioXException("ARDUINO_UPLOAD_CHANGED", "上传快照与确认的固件不一致。");
            }
            // 仅上传普通 HEX，不调用 burn-bootloader，不接受编程器或熔丝参数。
            arguments.AddRange(["--input-file", snapshot]);
        }
        try
        {
            var result = await execution.RunAsync(arguments, Path.GetDirectoryName(snapshot)!, log, token);
            var report = new ArduinoUploadReport(result, logPath, ArduinoUploadReport.HasVerification(preview.Board, result));
            log.Report("\n" + report.Summary + "\n");
            return report;
        }
        catch (Exception failure)
        {
            log.Report("\n上传未完成：" + failure + "\n");
            throw;
        }
    }

    public async Task<ArduinoLibrary[]> InstallLibraryAsync(string root, ArduinoLibrary requested,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        requested.Validate();
        var original = await FileHashAsync(PathBoundary.Resolve(root, ".studiox/project.json"), token);
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token, output);
        await RequireLibrariesAsync(execution, project.Arduino!, token);
        await CheckDependenciesAsync(root, execution, token);
        var stage = PathBoundary.Resolve(dataDirectory, "arduino/library-staging/" + Guid.NewGuid().ToString("N"));
        await ArduinoRuntime.CopyAsync(execution.LibraryRoot, stage, token);
        execution.LibraryRoot = stage;
        var update = await execution.RunAsync(["lib", "update-index"], root, output, token);
        RequireSuccess(update);
        var installed = await execution.RunAsync(["lib", "install", requested.Name + "@" + requested.Version], root, output, token);
        RequireSuccess(installed);
        var libraries = await ReadLibrariesAsync(execution, root, token);
        if (!libraries.Contains(requested))
        {
            throw new StudioXException("ARDUINO_LIBRARY", "CLI 未安装明确请求的库版本。原工程依赖保持不变。");
        }
        var settings = project.Arduino! with
        {
            Libraries = libraries
        };
        settings.Validate();
        var target = runtime.LibraryRoot(settings);
        var digest = await LibrariesHashAsync(execution, token);
        if (Directory.Exists(target))
        {
            if (digest != await ArduinoSourceSnapshot.HashAsync(target, false, token))
            {
                throw new StudioXException("ARDUINO_LIBRARY_CHANGED", "相同库版本已有不同源码，拒绝覆盖已有库集合。");
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(stage, target);
        }
        if (original != await FileHashAsync(PathBoundary.Resolve(root, ".studiox/project.json"), token))
        {
            throw new StudioXException("ARDUINO_PROJECT_CHANGED", "安装期间工程配置发生变化；已下载库保留，工程配置未被覆盖。");
        }
        File.Delete(PathBoundary.Resolve(root, ArduinoBuildReceipt.RelativePath));
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ".studiox/project.json"), project with
        {
            Arduino = settings
        }, token);
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ArduinoDependencyLock.RelativePath),
            new ArduinoDependencyLock(1, execution.Tools.Fingerprint, digest), token);
        return libraries;
    }

    private static async Task<ProjectManifest> RequireAsync(string root, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        if (project.Kind != ProjectKind.Arduino)
        {
            throw new StudioXException("ARDUINO_PROJECT", "此操作只适用于 Arduino 工程。");
        }
        if (File.Exists(PathBoundary.Resolve(root, "sketch.yaml")))
        {
            throw new StudioXException("ARDUINO_PROFILE", "StudioX 使用工程锁定的核心与库；请将外部 sketch.yaml 移至 .studiox/import/ 保存，避免隐式 profile 改选依赖。");
        }
        return project;
    }

    public async Task RestoreLibrariesAsync(string root, IProgress<string>? output = null, CancellationToken token = default)
    {
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token);
        var target = execution.LibraryRoot;
        execution.LibraryRoot = PathBoundary.Resolve(dataDirectory, "arduino/library-staging/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(execution.LibraryRoot);
        if (project.Arduino!.Libraries.Length > 0)
        {
            RequireSuccess(await execution.RunAsync(["lib", "update-index"], root, output, token));
        }
        foreach (var library in project.Arduino.Libraries)
        {
            RequireSuccess(await execution.RunAsync(["lib", "install", "--no-deps", library.Name + "@" + library.Version], root, output, token));
        }
        await RequireLibrariesAsync(execution, project.Arduino, token);
        await CheckDependenciesAsync(root, execution, token);
        var backup = target + ".recovery-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(target))
        {
            Directory.Move(target, backup);
        }
        try
        {
            Directory.Move(execution.LibraryRoot, target);
        }
        catch
        {
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
            throw;
        }
    }

    public async Task<ArduinoLibrarySearchResult[]> SearchLibrariesAsync(string root, string query, CancellationToken token = default)
    {
        new ArduinoLibrary(query, "0.0.0").Validate();
        var project = await RequireAsync(root, token);
        using var execution = await runtime.OpenAsync(project.Arduino!, token);
        RequireSuccess(await execution.RunAsync(["lib", "update-index"], root, null, token));
        var result = await execution.RunAsync(["lib", "search", query, "--json"], root, null, token);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("libraries").EnumerateArray().Take(50).Select(item =>
        {
            var latest = item.GetProperty("latest");
            return new ArduinoLibrarySearchResult(item.GetProperty("name").GetString()!, latest.GetProperty("version").GetString()!,
                latest.TryGetProperty("sentence", out var sentence) ? sentence.GetString() ?? "" : "");
        }).ToArray();
    }

    private static async Task RequireLibrariesAsync(ArduinoRuntime.Execution execution, ArduinoProjectSettings settings, CancellationToken token)
    {
        Directory.CreateDirectory(execution.LibraryRoot);
        var actual = await ReadLibrariesAsync(execution, execution.LibraryRoot, token);
        if (!settings.Libraries.OrderBy(item => item.Name, StringComparer.Ordinal)
            .SequenceEqual(actual.OrderBy(item => item.Name, StringComparer.Ordinal)))
        {
            throw new StudioXException("ARDUINO_LIBRARY_MISSING", "锁定的 Arduino 库未安装或版本不一致，请在 Arduino 管理页恢复工程依赖。");
        }
    }

    private static async Task<ArduinoLibrary[]> ReadLibrariesAsync(ArduinoRuntime.Execution execution, string workingDirectory, CancellationToken token)
    {
        var result = await execution.RunAsync(["lib", "list", "--json"], workingDirectory, null, token);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("installed_libraries").EnumerateArray()
            .Select(item => item.GetProperty("library"))
            .Select(item => new ArduinoLibrary(item.GetProperty("name").GetString()!, item.GetProperty("version").GetString()!))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<ArduinoDependencyLock> CheckDependenciesAsync(string root, ArduinoRuntime.Execution execution, CancellationToken token)
    {
        var actual = new ArduinoDependencyLock(1, execution.Tools.Fingerprint, await LibrariesHashAsync(execution, token));
        var path = PathBoundary.Resolve(root, ArduinoDependencyLock.RelativePath);
        if (File.Exists(path))
        {
            if (await JsonStore.ReadAsync<ArduinoDependencyLock>(path, token) != actual)
            {
                throw new StudioXException("ARDUINO_DEPENDENCY_CHANGED", "工具或库的源码内容与工程内容锁不一致，拒绝编译/上传。");
            }
        }
        else
        {
            await JsonStore.WriteAsync(path, actual, token);
        }
        return actual;
    }

    private static async Task<ArduinoUploadPreview> ValidateReceiptAsync(string root, ProjectManifest project,
        ArduinoRuntime.Execution execution, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, ArduinoBuildReceipt.RelativePath);
        if (!File.Exists(path))
        {
            throw new StudioXException("ARDUINO_BUILD_REQUIRED", "请先成功编译当前 Arduino 工程。");
        }
        var receipt = await JsonStore.ReadAsync<ArduinoBuildReceipt>(path, token);
        var dependencies = await CheckDependenciesAsync(root, execution, token);
        var hex = PathBoundary.Resolve(root, receipt.HexRelativePath);
        if (receipt.FormatVersion != 1 || !receipt.HexRelativePath.StartsWith(".build/arduino/", StringComparison.Ordinal) ||
            !receipt.HexRelativePath.EndsWith("/output/" + project.Name + ArduinoBoard.Require(project.Arduino!.BoardId).FirmwareSuffix, StringComparison.Ordinal) ||
            receipt.SourceSha256 != await ArduinoSourceSnapshot.HashAsync(root, true, token) ||
            receipt.ToolFingerprint != execution.Tools.Fingerprint || receipt.LibrariesSha256 != dependencies.LibrariesSha256 ||
            !File.Exists(hex) || receipt.HexSha256 != await FileHashAsync(hex, token) || project != await RequireAsync(root, token))
        {
            throw new StudioXException("ARDUINO_BUILD_STALE", "工程、依赖或固件已变化，请重新编译后再上传。");
        }
        if (project.Arduino!.Platform.Id == "esp32")
        {
            if (receipt.UploadFiles is not { Count: > 0 and <= 32 } || !RequiredEspImages(project.Name).All(receipt.UploadFiles.ContainsKey))
            {
                throw new StudioXException("ARDUINO_BUILD_STALE", "ESP32 构建的多镜像记录不完整，请重新编译。");
            }
            foreach (var (name, hash) in receipt.UploadFiles)
            {
                if (Path.GetFileName(name) != name || !name.EndsWith(".bin", StringComparison.Ordinal) ||
                    !File.Exists(PathBoundary.Resolve(Path.GetDirectoryName(hex)!, name)) ||
                    await FileHashAsync(PathBoundary.Resolve(Path.GetDirectoryName(hex)!, name), token) != hash)
                {
                    throw new StudioXException("ARDUINO_BUILD_STALE", "ESP32 镜像已变化，请重新编译后再上传：" + name);
                }
            }
        }
        var approval = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt))));
        return new(ArduinoBoard.Require(project.Arduino!.BoardId), hex, receipt.HexSha256, new FileInfo(hex).Length, approval);
    }

    private static string[] RequiredEspImages(string name) => [name + ".ino.bin", name + ".ino.bootloader.bin", name + ".ino.partitions.bin"];

    private static async Task RequireSameProjectAsync(string root, ProjectManifest expected, CancellationToken token)
    {
        if (expected != await RequireAsync(root, token))
        {
            throw new StudioXException("ARDUINO_PROJECT_CHANGED", "工具准备期间工程配置发生变化，请重新操作；不会使用旧板型构建。");
        }
    }

    private static Task<string> LibrariesHashAsync(ArduinoRuntime.Execution execution, CancellationToken token) =>
        ArduinoSourceSnapshot.HashAsync(execution.LibraryRoot, false, token);

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static void RequireSuccess(ProcessResult result)
    {
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new StudioXException("ARDUINO_CLI", $"Arduino CLI 失败（退出码 {result.ExitCode}，超时 {result.TimedOut}）：\n{result.StandardOutput}\n{result.StandardError}");
        }
    }
}

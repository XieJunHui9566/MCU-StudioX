namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>复用所选 SDK 或历史组件的构建入口；IDE 不复制编译器参数规则。</summary>
internal static class Mx251BuildService
{
    internal static void Invalidate(string root)
    {
        // 构建前置校验也可能失败；公共路径和内部 latest 都不得留下可误选的旧固件或成功凭据。
        foreach (var relative in new[] { BuildReceipt.RelativePath, BuildMemoryService.SnapshotPath, Mx251BuildStorage.Firmware,
            ".build/firmware.bin", ".build/mx251/latest/firmware.bin", ".build/mx251/latest/linked.partial.hex",
            ".build/mx251/latest/firmware.hex", ".build/mx251/latest/receipt.json" })
        {
            var file = PathBoundary.Resolve(root, relative);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    internal static async Task<BuildReport> ExecuteAsync(string root, ProjectManifest project, ResolvedToolset tools,
        IProgress<string>? output, CancellationToken token, ResolvedToolset? buildTools = null, bool configureOnly = false, string? outputLease = null)
    {
        Mx251Profile.ValidateTools(tools);
        var qualified = Mx251SdkProfile.Matches(project);
        if (project.Kind != ProjectKind.Pack || (!qualified && project.DeviceId != "AI8051U-34K64") || project.Espressif is not null ||
            project.Logic is not null || project.PinMapping is not null || project.GraphicalConfiguration is not null)
        {
            throw new StudioXException("MX251_TARGET", "该组件需要与所选目标匹配的 MX251 器件工程。");
        }
        if ((await ProjectBuildSettings.ReadAsync(root, token)).HasOverrides)
        {
            throw new StudioXException("MX251_BUILD_SETTINGS", "MX251 参数请写入 mx251.json；不能忽略已有通用编译参数覆盖。");
        }
        var configuration = PathBoundary.Resolve(root, Mx251Profile.ConfigurationFile);
        if (!File.Exists(configuration))
        {
            throw new StudioXException("MX251_CONFIGURATION", "缺少 mx251.json。请使用配套器件模板创建工程。");
        }
        var sourceStamp = await DebugSourceStamp.ComputeAsync(root, token);
        if (qualified)
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configuration, token));
            if (config.RootElement.GetProperty("target").GetString() != Mx251SdkProfile.Target(project.DeviceId))
            {
                throw new StudioXException("MX251_TARGET", "工程器件与 MX251 目标配置不一致。");
            }
            if (!Mx251CMake.Enabled(root))
            {
                throw new StudioXException("MX251_CMAKE", "MX251 当前组件需要 CMakeLists.txt 管理工程输入。");
            }
        }
        else
        {
            await Mx251ClockConfiguration.ReadAsync(root, token);
        }
        var run = qualified ? PathBoundary.Resolve(root, ".build/mx251/latest") : Mx251BuildStorage.Prepare(root, download: false);
        var logPath = PathBoundary.Resolve(root, ".build/studiox-build.log");
        Directory.CreateDirectory(Path.GetDirectoryName(run)!);
        var log = new StringBuilder("MX251：锁定构建入口，输出 " + run + Environment.NewLine);
        var environment = ToolsetEnvironment.Create(tools);
        if (qualified)
        {
            environment[Mx251OutputLease.EnvironmentKey] = outputLease ?? throw new StudioXException("MX251_BUSY", "缺少输出租约。");
        }
        environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        environment["POWERSHELL_UPDATECHECK"] = "Off";
        environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(tools.Tool("pwsh"))!, "Modules");
        var firmware = Path.Combine(run, "firmware.hex");
        var published = false;
        try
        {
            // 新组件自带独立入口；IDE 只传路径与生命周期租约，不覆盖工具链内部规则。
            var support = qualified ? Path.GetDirectoryName(tools.Tool("build251"))! : await Mx251CMake.WriteSupportAsync(root, token);
            ProcessRequest command;
            if (Mx251CMake.Enabled(root))
            {
                if (buildTools is null || (qualified ? buildTools.Manifest.Purpose != "cmake-build" : buildTools.Manifest.Version != Mx251CMake.Version))
                {
                    throw new StudioXException("MX251_CMAKE_TOOLS", "此工程需要显式锁定的 CMake + Ninja 构建组件，请检查工程开发环境组件声明。");
                }
                var configured = await new ProcessRunner().RunAsync(Mx251CMake.Configure(root, support, tools, buildTools, environment, output, project), token);
                log.AppendLine(configured.StandardOutput);
                log.AppendLine(configured.StandardError);
                if (!configured.Success || configureOnly)
                {
                    return new(configured.Success, log.ToString(), [], logPath, configured.ExitCode, configured.TimedOut);
                }
                command = new(buildTools.Tool("cmake"), ["--build", PathBoundary.Resolve(root, ".build/mx251-cmake"), "--target", "firmware"],
                    root, TimeSpan.FromMinutes(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output, ContainProcessTree: qualified);
            }
            else
            {
                if (configureOnly)
                {
                    throw new StudioXException("MX251_CMAKE", "旧工程尚无 CMakeLists.txt，请先迁移工程配置。");
                }
                command = new(tools.Tool("pwsh"), ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(support, "Build.ps1"),
                    "-Project", configuration, "-ToolchainRoot", tools.ResourceDirectory("sdcc"), "-ProfileRoot", tools.ResourceDirectory("profile"),
                    "-OutputDirectory", run], root, TimeSpan.FromMinutes(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output);
            }
            var result = await new ProcessRunner().RunAsync(command, token);
            log.AppendLine(result.StandardOutput);
            log.AppendLine(result.StandardError);
            // 原入口为每个进程保存原始诊断；同时汇入 IDE 输出，让用户可直接定位第一个编译错误。
            if (Directory.Exists(run))
            {
                foreach (var file in Directory.EnumerateFiles(run, "*.log").Order(StringComparer.Ordinal))
                {
                    var text = await File.ReadAllTextAsync(file, token);
                    if (text.Length == 0)
                    {
                        continue;
                    }
                    var diagnostic = Path.GetFileName(file) + Environment.NewLine + text;
                    log.AppendLine(diagnostic);
                    output?.Report(diagnostic);
                }
            }
            if (!result.Success)
            {
                return new(false, log.ToString(), [], logPath, result.ExitCode, result.TimedOut);
            }
            Mx251NakedDiagnostics.Validate(run);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "receipt.json"), token));
            using var image = File.OpenRead(firmware);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(image, token));
            if (receipt.RootElement.GetProperty("status").GetString() != "complete" ||
                !digest.Equals(receipt.RootElement.GetProperty("firmwareSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MX251_ARTIFACT", "MX251 入口未发布完整且匹配凭据的固件。");
            }
            if (qualified)
            {
                using var binary = File.OpenRead(Path.Combine(run, "firmware.bin"));
                var binaryRecord = receipt.RootElement.GetProperty("binary");
                if (binaryRecord.GetProperty("file").GetString() != "firmware.bin" ||
                    binary.Length != binaryRecord.GetProperty("length").GetInt64() ||
                    !Convert.ToHexString(await SHA256.HashDataAsync(binary, token)).Equals(binaryRecord.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("MX251_ARTIFACT", "BIN 与本次构建凭据不一致。");
                }
            }
            if (project != await ProjectService.ReadAsync(root, token) || sourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
            {
                throw new StudioXException("MX251_SOURCE_CHANGED", "构建期间源码或配置发生变化，本次固件不作为当前工程的成功产物发布。");
            }
            await BuildReceipt.WriteAsync(root, project, tools.Fingerprint, [firmware], token, sourceStamp,
                [Path.Combine(run, "linked.partial.map"), Path.Combine(run, "linked.partial.mem"), Path.Combine(run, "receipt.json")]);
            var publicFirmware = PathBoundary.Resolve(root, Mx251BuildStorage.Firmware);
            File.Copy(firmware, publicFirmware, overwrite: true);
            if (qualified)
            {
                File.Copy(Path.Combine(run, "firmware.bin"), PathBoundary.Resolve(root, ".build/firmware.bin"), overwrite: true);
            }
            published = true;
            var message = "固件输出：" + publicFirmware;
            log.AppendLine(message);
            output?.Report(message);
            log.AppendLine("编译成功；硬件未连接。原始命令、诊断、启动/栈检查与固件哈希已保存。");
            return new(true, log.ToString(), [firmware, publicFirmware, .. qualified ? new[] { PathBoundary.Resolve(root, ".build/firmware.bin") } : Array.Empty<string>(), Path.Combine(run, "linked.partial.map"), Path.Combine(run, "receipt.json")], logPath, result.ExitCode);
        }
        catch (Exception error)
        {
            log.AppendLine(error.Message);
            throw;
        }
        finally
        {
            if (!published)
            {
                Invalidate(root);
            }
            await File.WriteAllTextAsync(logPath, log.ToString(), CancellationToken.None);
        }
    }
}

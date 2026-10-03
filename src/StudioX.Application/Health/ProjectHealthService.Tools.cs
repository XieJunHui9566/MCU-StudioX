namespace StudioX.Application.Health;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ProjectHealthService
{
    private async Task<ResolvedToolset?> InspectToolsAsync(string id, string version, string compiler, string? target, bool deep,
        List<HealthCheck> checks, IProgress<string>? progress, CancellationToken token)
    {
        var title = "开发环境组件：" + id + " / " + version;
        try
        {
            progress?.Report("检查 " + title + "…");
            StudioX.Packages.PackValidator.Token(id);
            StudioX.Packages.PackValidator.Version(version);
            var root = PathBoundary.Resolve(catalog.RootDirectory, id + "/" + version);
            var path = PathBoundary.Resolve(root, "toolset.json");
            catalog.RequireEnabled(id, version);
            if (!File.Exists(path)) throw new StudioXException("TOOLSET_MISSING", "缺少开发环境组件：" + id + " / " + version + "。请通过“准备工程开发环境组件”导入对应 .mcutoolchain。");
            var bytes = await ReadLimitedAsync(path, 64 * 1024 * 1024, token);
            var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
            var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
                ?? throw new StudioXException("TOOLSET_MANIFEST", "工具清单为空。");
            if (manifest.FormatVersion != 1 || manifest.Id != id || manifest.Version != version || manifest.CompilerId != compiler || manifest.Host != "win-x64")
                throw new StudioXException("TOOLSET_INCOMPATIBLE", "开发环境组件格式、ID、版本、宿主或编译器与工程不一致。");
            if (manifest.Executables is null || manifest.Sha256 is null || manifest.Sha256.Count == 0)
                throw new StudioXException("TOOLSET_MANIFEST", "工具入口或哈希索引缺失。");
            var resolved = new ResolvedToolset(manifest, root, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            if (target is not null)
            {
                resolved = resolved.ForEspressifTarget(target);
                var suffix = target is "esp32c3" or "esp32c5" or "esp32c6" or "esp32p4" ? "riscv" : target;
                // 通用入口可能指向另一个芯片；健康检查必须确认工程目标自己的工具角色。
                foreach (var role in new[] { "gcc", "gxx", "objcopy", "size" })
                    if (!manifest.Executables.ContainsKey(role + "-" + suffix))
                        throw new StudioXException("TOOL_ROLE", "开发环境组件缺少目标 " + target + " 的组件：" + role);
            }
            var roles = manifest.Purpose switch
            {
                "ag32-mapping" => new[] { "python", "converter", "supra" }, "hdl-native" => ["mapper"], "hdl-simulation" => ["iverilog", "vvp"],
                "windows-native" => ["gcc", "gxx", "ar", "ranlib", "as", "ld", "objcopy", "objdump", "size"],
                _ when id == "stc.sdcc" => ["cmake", "ninja", "sdcc", "sdar", "sdas8051", "sdld", "packihx"],
                _ => ["cmake", "ninja", "gcc", "gxx", "objcopy", "size"]
            };
            if (target is not null) roles = roles.Concat(["python", "git"]).ToArray();
            foreach (var role in roles)
            {
                var executable = resolved.Tool(role);
                var relative = Path.GetRelativePath(root, executable).Replace('\\', '/');
                if (!File.Exists(executable) || !manifest.Sha256.ContainsKey(relative))
                    throw new StudioXException("TOOL_MISSING", "工具入口缺失或未索引：" + role + " · " + executable);
            }
            if (deep)
            {
                var verified = await catalog.ResolveAsync(id, version, compiler, token, true, progress);
                if (verified.Fingerprint != resolved.Fingerprint) throw new StudioXException("TOOL_CHANGED", "检查期间工具清单发生变化，请重新检查。");
                resolved = target is null ? verified : verified.ForEspressifTarget(target);
                checks.Add(new("HEALTH_TOOL_HASH", title, HealthState.Passed, $"完整哈希校验通过，共 {manifest.Sha256.Count:N0} 个索引文件。",
                    "tool-environment", HealthAction.Tools, ToolsetId: id, ToolsetVersion: version));
                // 只调用固定的 --version，不执行配置、用户源码或探针连接命令。
                foreach (var role in roles.Where(role => role is "gcc" or "sdcc" or "cmake" or "ninja" or "python" or "git"))
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report("启动检查：" + role + "…");
                    var environment = ToolsetEnvironment.Create(resolved);
                    if (role == "python" && manifest.ResourceDirectories?.ContainsKey("python-env") == true)
                        environment["PYTHONHOME"] = resolved.ResourceDirectory("python-env");
                    var result = await new ProcessRunner().RunAsync(new(resolved.Tool(role), ["--version"], root, TimeSpan.FromSeconds(15),
                        environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
                    var raw = result.StandardOutput + result.StandardError;
                    checks.Add(new(result.Success ? "HEALTH_TOOL_START" : "TOOL_EXECUTE", "启动：" + role, result.Success ? HealthState.Passed : HealthState.Error,
                        result.Success ? "--version 启动成功。" : $"启动失败：exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}。",
                        "tool-environment", HealthAction.Tools, raw, id, version));
                }
            }
            else checks.Add(new("HEALTH_TOOL_ENTRIES", title, HealthState.Information,
                "工具身份与必要入口存在；未校验文件内容。手动“深度工具检查”或实际构建会进行完整哈希校验。",
                "tool-environment", HealthAction.Tools, ToolsetId: id, ToolsetVersion: version));
            return resolved;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { checks.Add(Failure("HEALTH_TOOLS", title, "tool-environment", HealthAction.Tools, error, id, version)); return null; }
    }
}

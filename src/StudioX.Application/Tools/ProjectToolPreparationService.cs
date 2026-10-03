namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.Distribution;
using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>按明确工程配置准备工具；快速入口不遍历 SDK、不运行程序、不更改工程锁。</summary>
public sealed partial class ProjectToolPreparationService(ToolsetCatalog catalog, ToolManagementService management)
{
    private sealed record Need(string Id, string Version, string Compiler, string Purpose);
    private static readonly string[] ConfigurationFiles = [".studiox/project.json", ".studiox/toolchain.lock.json",
        DevelopmentComponentLock.RelativePath, "device/manifest.json", ".studiox/ag32-mapping-toolchain.lock.json",
        ".studiox/ag32-logic-toolchain.lock.json", HdlSimulationSettings.RelativePath];

    public Task<ProjectToolPlan> InspectAsync(string? directory, DistributionListing? listing = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            if (directory is null) return new ProjectToolPlan(null, "尚未选择工程", "",
                "从新建工程页选择器件包、准确器件和模板创建工程，或选择已有 StudioX 工程。缺少器件包时可导入 .mcupack；开发环境组件版本由工程明确指定。", []);
            var root = Path.GetFullPath(directory);
            var fingerprint = await FingerprintAsync(root, token);
            var project = await ProjectService.ReadAsync(root, token);
            if (project.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr)
                return new ProjectToolPlan(root, project.Name, fingerprint, project.Kind == ProjectKind.MicroPython
                    ? "MicroPython 脚本工程不需要安装原生 GCC/CMake 开发环境组件。请使用 MicroPython 页面。"
                    : "Zephyr 当前为实验工程，本页不自动准备其外部环境。请阅读框架帮助。", []);
            var components = await ProjectDevelopmentComponents.ReadAsync(root, project, token);
            var pins = await ProjectDevelopmentComponents.ReadPinsAsync(root, components, token);
            var needs = components.Select(item => new Need(item.Id, item.Version, item.CompilerId, item.Purpose)).ToList();
            if (File.Exists(PathBoundary.Resolve(root, HdlSimulationSettings.RelativePath)))
                needs.Add(new("hdl.iverilog", "14.0.0", "iverilog", "已配置的 RTL 仿真"));
            var result = new List<ProjectToolRequirement>();
            foreach (var need in needs.DistinctBy(n => (n.Id, n.Version, n.Compiler)))
            {
                token.ThrowIfCancellationRequested();
                PackValidator.Token(need.Id); PackValidator.Version(need.Version);
                var locked = pins.GetValueOrDefault(need.Id);
                var entry = listing?.Catalog.Entries.SingleOrDefault(e => e.Kind == "tool" && e.Id == need.Id && e.Version == need.Version);
                var folder = PathBoundary.Resolve(catalog.RootDirectory, need.Id + "/" + need.Version);
                var state = Directory.Exists(folder) ? ProjectToolState.RepairNeeded : ProjectToolState.Missing;
                var diagnostic = state == ProjectToolState.Missing ? "未安装工程锁定版本，可选择目录或离线归档。" : "版本目录存在但清单缺失，请使用离线修复入口。";
                if (File.Exists(Path.Combine(folder, "toolset.json")))
                {
                    try
                    {
                        await CheckEntrypointsAsync(folder, need, locked, need.Id == project.ToolsetId ? project.Espressif?.Target : null, token);
                        state = ProjectToolState.Installed;
                        diagnostic = "清单身份和入口存在；尚未完整校验 SDK 文件。构建前仍执行完整内容校验。";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { diagnostic = error.ToString(); }
                }
                if (state == ProjectToolState.Installed && !catalog.IsEnabled(need.Id, need.Version))
                {
                    state = ProjectToolState.Disabled;
                    diagnostic = "组件已安装但被禁用。请在开发环境组件管理中启用此精确版本；不会改用其他版本或重新下载。";
                }
                result.Add(new(need.Id, need.Version, need.Compiler, need.Purpose, state, diagnostic, locked, entry));
            }
            if (fingerprint != await FingerprintAsync(root, token)) throw new StudioXException("TOOLS_PROJECT_CHANGED", "检查期间工程配置发生变化，请刷新。");
            return new ProjectToolPlan(root, project.Name, fingerprint,
                $"{result.Count(r => r.State == ProjectToolState.Missing)} 个开发环境组件未安装，{result.Count(r => r.State == ProjectToolState.RepairNeeded)} 个需要修复，{result.Count(r => r.State == ProjectToolState.Disabled)} 个已禁用。仅检查明确版本和入口，不扫描 SDK，不自动下载。", result);
        }, token);

    public async Task<ToolArchivePreview> PreviewAsync(ProjectToolPlan plan, ProjectToolRequirement requirement, string archive,
        IProgress<string>? progress = null, CancellationToken token = default, bool verifyCatalog = true)
    {
        await EnsureCurrentAsync(plan, requirement, token);
        var preview = await management.PreviewInstallAsync(archive, progress, token);
        EnsureIdentity(requirement, preview, verifyCatalog);
        return preview;
    }

    public async Task InstallAsync(ProjectToolPlan plan, ProjectToolRequirement requirement, ToolArchivePreview preview,
        IProgress<string>? progress = null, CancellationToken token = default, bool verifyCatalog = true)
    {
        await EnsureCurrentAsync(plan, requirement, token);
        EnsureIdentity(requirement, preview, verifyCatalog);
        if (InstallSpacePlan(preview).Any(p => p.RequiredBytes > p.AvailableBytes))
            throw new StudioXException("INSTALL_SPACE", "安装暂存空间不足。");
        await management.InstallAsync(preview, progress, token);
    }

    private async Task EnsureCurrentAsync(ProjectToolPlan plan, ProjectToolRequirement requirement, CancellationToken token)
    {
        if (plan.ProjectDirectory is null || !plan.Requirements.Contains(requirement) || requirement.State != ProjectToolState.Missing)
            throw new StudioXException("TOOLS_SELECTION", "请选择当前工程未安装的开发环境组件；已有目录需要使用修复入口。");
        if (await FingerprintAsync(plan.ProjectDirectory, token) != plan.Fingerprint)
            throw new StudioXException("TOOLS_PROJECT_CHANGED", "工程配置在预览后发生变化，请刷新后重试。");
        var current = await InspectAsync(plan.ProjectDirectory, token: token);
        if (current.Fingerprint != plan.Fingerprint || !current.Requirements.Any(r => r.Id == requirement.Id && r.Version == requirement.Version
            && r.CompilerId == requirement.CompilerId && r.LockedFingerprint == requirement.LockedFingerprint && r.State == ProjectToolState.Missing))
            throw new StudioXException("TOOLS_PROJECT_CHANGED", "工程配置或工具状态在预览后发生变化，请刷新后重试。");
    }

    private static void EnsureIdentity(ProjectToolRequirement need, ToolArchivePreview preview, bool verifyCatalog)
    {
        if (preview.Id != need.Id || preview.Version != need.Version || preview.CompilerId != need.CompilerId
            || need.LockedFingerprint is { } pin && !pin.Equals(preview.Fingerprint, StringComparison.OrdinalIgnoreCase)
            || verifyCatalog && need.Entry is { } entry && (!entry.Sha256.Equals(preview.ArchiveSha256, StringComparison.OrdinalIgnoreCase) || entry.InstalledBytes != preview.Bytes))
            throw new StudioXException("TOOLS_PROJECT_IDENTITY", "归档与工程的精确版本、编译器、内容锁或目录声明不一致，未安装。");
    }

    public IReadOnlyList<DistributionSpacePlan> InstallSpacePlan(ToolArchivePreview preview)
    {
        var drive = Path.GetPathRoot(catalog.RootDirectory)!;
        return [new(drive, checked(2 * preview.Bytes), new DriveInfo(drive).AvailableFreeSpace)];
    }

    private static async Task CheckEntrypointsAsync(string folder, Need need, string? pin, string? target, CancellationToken token)
    {
        var path = Path.Combine(folder, "toolset.json");
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new StudioXException("TOOLSET_MANIFEST", "工具清单过大。");
        var bytes = await File.ReadAllBytesAsync(path, token);
        var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
            ?? throw new StudioXException("TOOLSET_MANIFEST", "工具清单为空。");
        if (manifest.FormatVersion != 1 || manifest.Id != need.Id || manifest.Version != need.Version || manifest.CompilerId != need.Compiler || manifest.Host != "win-x64"
            || manifest.Executables is null || manifest.Sha256 is null)
            throw new StudioXException("TOOLSET_INCOMPATIBLE", "工具清单与工程指定的身份或编译器不一致。");
        if (pin is not null && !pin.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("TOOLCHAIN_LOCK", "工具清单指纹与工程内容锁不一致，请恢复相同内容的开发环境组件。");
        var roles = manifest.Purpose switch
        {
            "ag32-mapping" => new[] { "python", "converter", "supra" }, "hdl-native" => ["mapper"], "hdl-simulation" => ["iverilog", "vvp"],
            "windows-native" => ["gcc", "gxx", "ar", "ranlib", "as", "ld", "objcopy", "objdump", "size"],
            _ when need.Id == "stc.sdcc" => ["cmake", "ninja", "sdcc", "sdar", "sdas8051", "sdld", "packihx"],
            _ => ["cmake", "ninja", "gcc", "gxx", "objcopy", "size"]
        };
        var resolved = new ResolvedToolset(manifest, folder, "");
        if (manifest.Purpose is "esp-idf" or "esp8266-rtos-sdk")
        {
            roles = roles.Concat(["python", "git"]).ToArray();
            foreach (var resource in new[] { "idf", "tools", "python-env" })
                if (!Directory.Exists(resolved.ResourceDirectory(resource))) throw new StudioXException("TOOL_RESOURCE", "SDK 目录缺失：" + resource);
            if (target is not null)
            {
                var suffix = target is "esp32c3" or "esp32c5" or "esp32c6" or "esp32p4" ? "riscv" : target;
                foreach (var role in new[] { "gcc", "gxx", "objcopy", "size" })
                    if (!manifest.Executables.ContainsKey(role + "-" + suffix)) throw new StudioXException("TOOL_ROLE", "缺少工程目标的工具：" + role + "-" + suffix);
                resolved = resolved.ForEspressifTarget(target);
            }
        }
        foreach (var role in roles)
        {
            token.ThrowIfCancellationRequested();
            var executable = resolved.Tool(role);
            if (!File.Exists(executable) || !manifest.Sha256.ContainsKey(Path.GetRelativePath(folder, executable).Replace('\\', '/')))
                throw new StudioXException("TOOL_MISSING", "工具入口缺失或未索引：" + role);
        }
    }

    private static async Task<string> FingerprintAsync(string root, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in ConfigurationFiles)
        {
            var path = PathBoundary.Resolve(root, relative);
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative + "\n"));
            if (!File.Exists(path)) { hash.AppendData([0]); continue; }
            if (new FileInfo(path).Length > (relative == "device/manifest.json" ? 32 : 1) * 1024 * 1024)
                throw new StudioXException("TOOLS_PROJECT_SIZE", "工程配置文件过大：" + relative);
            hash.AppendData([1]); hash.AppendData(await File.ReadAllBytesAsync(path, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

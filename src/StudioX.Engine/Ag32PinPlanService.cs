namespace StudioX.Engine;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>先验证实际转换结果再提交 VE；图形编辑不运行布局器、不访问硬件。</summary>
public sealed class Ag32PinPlanService(ToolsetCatalog tools)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PreviewFiles = ["pins.ve", "pins.hx", "pins.vx", "pins.vex", "studiox-clocks.sdc", "studiox-gpio.asf", "converter.log", "analog_ip.vx", "analog_ip.asf"];

    public Task<Ag32PinPlanSnapshot> ReadAsync(string projectDirectory, CancellationToken cancellationToken = default)
        => Task.Run(async () => (await ReadContextAsync(Path.GetFullPath(projectDirectory), cancellationToken)).Snapshot, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, string expectedSourceSha256,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => Task.Run(() => ApplyCoreAsync(Path.GetFullPath(projectDirectory), expectedSourceSha256, null, assignments.ToArray(), clocks, null, cancellationToken), cancellationToken);

    /// <summary>界面草稿与审批绑定完整目标身份，VE 内容相同也不能跨型号提交。</summary>
    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, Ag32PinPlanSnapshot expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => Task.Run(() => ApplyCoreAsync(Path.GetFullPath(projectDirectory), expectedSnapshot.SourceSha256,
            expectedSnapshot, assignments.ToArray(), clocks, null, cancellationToken), cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, Ag32PinPlanSnapshot expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ApplyCoreAsync(Path.GetFullPath(projectDirectory), expectedSnapshot.SourceSha256,
            expectedSnapshot, assignments.ToArray(), clocks, analog, cancellationToken), cancellationToken);

    private async Task<Ag32PinPlanResult> ApplyCoreAsync(string root, string expectedHash, Ag32PinPlanSnapshot? expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, Ag32AnalogSettings? analog, CancellationToken token)
    {
        if (expectedHash is null || !Regex.IsMatch(expectedHash, @"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("AG32_PIN_PLAN_STALE", "缺少有效的 VE 源文件散列，请重新加载图形规划。");
        }
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        string? staging = null;
        try
        {
            var context = await ReadContextAsync(root, token);
            var snapshot = context.Snapshot;
            if (expectedSnapshot is not null && (snapshot.DeviceId != expectedSnapshot.DeviceId ||
                snapshot.TargetDevice != expectedSnapshot.TargetDevice || snapshot.SourcePath != expectedSnapshot.SourcePath))
            {
                throw new StudioXException("AG32_PIN_PLAN_STALE", "工程型号、封装目标或 VE 路径已经改变，请重新加载图形规划。");
            }
            RequireHash(snapshot.SourceSha256, expectedHash);
            if (!snapshot.CanEdit)
            {
                throw new StudioXException("AG32_PIN_PLAN_READONLY", string.Join("\n", snapshot.Diagnostics));
            }
            context.Catalog.Validate(assignments, clocks);
            ValidateDeviceClock(Ag32DeviceCatalog.Require(snapshot.DeviceId), clocks);
            var source = Ag32PeripheralSupport.WithSettings(context.Document.Render(assignments, clocks), analog);
            Ag32PeripheralSupport.Validate(snapshot.DeviceId, source, assignments);
            var candidate = new Ag32PinPlanDocument(source);
            if (!candidate.CanEdit)
            {
                throw new StudioXException("AG32_PIN_PLAN_READONLY", string.Join("\n", candidate.Diagnostics));
            }
            var constraintsRoot = PathBoundary.Resolve(root, ".build/ag32-pin-plan");
            Directory.CreateDirectory(constraintsRoot);
            var relative = ".build/ag32-pin-plan/plan-" + Guid.NewGuid().ToString("N");
            staging = PathBoundary.Resolve(root, relative);
            Directory.CreateDirectory(staging);
            // 转换器不接受 BOM；仅去掉验证副本的 BOM，保存源仍使用原始编码格式。
            await File.WriteAllBytesAsync(PathBoundary.Resolve(staging, "pins.ve"), source is [0xef, 0xbb, 0xbf, ..] ? source[3..] : source, token);
            var macroArguments = await Ag32PeripheralSupport.PrepareLogicAsync(staging, source, token);
            var converter = await new ProcessRunner().RunAsync(new(context.Tools.Tool("python"),
                ["-I", "-B", "-X", "utf8", context.Tools.Tool("converter"), "-d", snapshot.TargetDevice,
                    .. macroArguments, "-c", "pins.hx", "pins.ve", "pins.vx", "-x", "pins.vex"], staging,
                TimeSpan.FromMinutes(2), ToolsetEnvironment.Create(context.Tools),
                RemoveEnvironment: ToolsetEnvironment.AmbientVariables.Append("ALTA_HOME").ToArray()), token);
            var diagnostics = converter.StandardOutput + converter.StandardError;
            await File.WriteAllTextAsync(PathBoundary.Resolve(staging, "converter.log"), diagnostics, token);
            if (!converter.Success || converter.OutputTruncated || diagnostics.Contains("is ignored because no IP macro is specified", StringComparison.Ordinal))
            {
                throw new StudioXException("AG32_PIN_PLAN_CONVERTER", "厂商转换未通过，VE 保持不变：\n" + diagnostics);
            }
            VerifyVex(await File.ReadAllTextAsync(PathBoundary.Resolve(staging, "pins.vex"), token), assignments);
            await Ag32GpioElectrical.CreateAsync(staging, source, token);
            await Ag32PinMappingClockVerification.CreateSdcAsync(staging, token, basicMapping: true);
            // 复用工具锁的身份验证，防止验证期间被替换的转换器生成可接受的预览。
            var afterTools = await tools.ResolveAsync(context.Settings.ToolsetId, context.Settings.ToolsetVersion, context.Settings.CompilerId, token);
            if (afterTools.Fingerprint != context.Tools.Fingerprint)
            {
                throw new StudioXException("AG32_PIN_PLAN_TOOL", "引脚转换工具在保存期间发生变化。");
            }
            var path = PathBoundary.Resolve(root, snapshot.SourcePath);
            var currentProject = await ProjectService.ReadAsync(root, token);
            var (currentSettings, currentProfile) = await RequireSettingsAsync(root, currentProject, token);
            if (currentProject.DeviceId != snapshot.DeviceId || currentSettings != context.Settings ||
                currentProfile.TargetDevice != snapshot.TargetDevice)
            {
                throw new StudioXException("AG32_PIN_PLAN_STALE", "工程目标在保存期间发生变化，请重新加载图形规划。");
            }
            var previousSource = await ReadSourceAsync(path, token);
            RequireHash(Hash(previousSource), expectedHash);
            var generated = Ag32SystemSupport.Render(source, await File.ReadAllTextAsync(PathBoundary.Resolve(staging, "pins.hx"), token), snapshot.Functions);
            await Ag32SystemSupport.WriteAsync(root, generated, snapshot.SourcePath, previousSource, source, token);
            var resultSnapshot = snapshot with
            {
                SourceSha256 = Hash(source),
                Assignments = candidate.Assignments,
                Clocks = candidate.Clocks,
                Analog = Ag32PeripheralSupport.Read(source),
                Diagnostics = [],
                CanEdit = true
            };
            var result = new Ag32PinPlanResult(resultSnapshot, staging, relative + "/pins.ve", relative + "/pins.vex",
                relative + "/studiox-clocks.sdc", diagnostics);
            var cleanupDiagnostics = TrimPreviews(constraintsRoot, staging);
            if (cleanupDiagnostics.Length != 0)
            {
                // 保存已完成；旧预览被占用等诊断必须对调用方可见，不将成功提交伪装成失败。
                result = result with
                {
                    ConverterDiagnostics = diagnostics + "\n" + cleanupDiagnostics
                };
            }
            staging = null;
            return result;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                RemovePreview(staging);
            }
            gate.Release();
        }
    }

    private async Task<Context> ReadContextAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new StudioXException("AG32_PIN_PLAN_DEVICE", "工程目录不存在或是重解析点。");
        }
        _ = PathBoundary.Resolve(root, ".studiox/project.json");
        var project = await ProjectService.ReadAsync(root, token);
        var (settings, profile) = await RequireSettingsAsync(root, project, token);
        var path = PathBoundary.Resolve(root, settings.PinMapFile);
        var bytes = await ReadSourceAsync(path, token);
        var resolved = await tools.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
        var lockPath = PathBoundary.Resolve(root, Ag32PinMappingReceipt.LockRelativePath);
        if (File.Exists(lockPath) && await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) !=
            new ToolchainLock(1, settings.ToolsetId, settings.ToolsetVersion, resolved.Fingerprint))
        {
            throw new StudioXException("AG32_PIN_PLAN_TOOL", "引脚规划工具与工程锁定内容不同。");
        }
        var catalog = await Ag32PinPlanCatalog.ReadAsync(resolved, settings.TargetDevice, profile.PinCount, root, token);
        var document = new Ag32PinPlanDocument(bytes);
        try
        {
            catalog.Validate(document.Assignments, document.Clocks);
            ValidateDeviceClock(profile, document.Clocks);
        }
        catch (StudioXException ex)
        {
            document.Diagnostics.Add(ex.Message + " 原文保留，图形规划只读。");
        }
        var snapshot = new Ag32PinPlanSnapshot(project.DeviceId, settings.TargetDevice, settings.PinMapFile, Hash(bytes),
            catalog.Pins, catalog.Functions, document.Assignments, document.Clocks, document.Diagnostics.ToArray(), document.CanEdit,
            Ag32PeripheralSupport.Read(bytes));
        return new(snapshot, document, catalog, resolved, settings);
    }

    private static async Task<(Ag32PinMappingProjectSettings Settings, Ag32DeviceProfile Profile)> RequireSettingsAsync(
        string root, ProjectManifest project, CancellationToken token)
    {
        var profile = Ag32DeviceCatalog.Find(project.DeviceId);
        if (project.Kind != ProjectKind.Pack || profile is null || !profile.CanMap || project.Logic is not null ||
            project.PinMapping is not { } settings || settings != new Ag32PinMappingProjectSettings(profile.TargetDevice))
        {
            throw new StudioXException("AG32_PIN_PLAN_DEVICE", "此图形规划需要已核实 AG32 器件封装、并启用基础映射的工程。");
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || pack.Vendor != "AGM" ||
            device is null || !profile.Matches(device) || device.ToolsetId != project.ToolsetId ||
            device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId)
        {
            throw new StudioXException("AG32_PIN_PLAN_DEVICE", "工程锁定的厂商、型号或工具与器件清单不一致。");
        }
        return (settings, profile);
    }

    private static async Task<byte[]> ReadSourceAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024)
        {
            throw new StudioXException("AG32_PIN_PLAN_SOURCE", "缺少 VE 文件，或文件超过基础引脚配置的 256 KiB 限制。");
        }
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > 256 * 1024)
        {
            throw new StudioXException("AG32_PIN_PLAN_SOURCE", "读取期间 VE 文件超过大小限制。");
        }
        return bytes;
    }

    private static void ValidateDeviceClock(Ag32DeviceProfile profile, Ag32PinClockSettings clocks)
    {
        if (clocks.SysMhz > profile.MaximumSysClockMhz || clocks.BusMhz > profile.MaximumSysClockMhz)
        {
            throw new StudioXException("AG32_PIN_PLAN_CLOCK",
                $"{profile.DeviceId} 的系统/总线时钟不能超过厂商手册核实的 {profile.MaximumSysClockMhz} MHz。");
        }
    }

    private static void VerifyVex(string text, IReadOnlyList<Ag32PinAssignment> assignments)
    {
        var actual = Regex.Matches(text, @"^\s*(\S+)\s+PIN_([0-9]+)(?:\s|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (actual.Length != assignments.Count || assignments.Any(item => actual.Count(pin => pin == item.PinNumber) != 1))
        {
            throw new StudioXException("AG32_PIN_PLAN_CONVERTER", "厂商 VEX 输出没有完整保留申请的物理引脚。");
        }
    }

    private static void RemovePreview(string directory)
    {
        // 验证副本只含本服务生成的平面文件，不递归删除或穿过用户创建的链接。
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new StudioXException("PATH_LINK", "预览目录不能是重解析点。");
        }
        foreach (var file in Directory.GetFiles(directory))
        {
            var path = PathBoundary.Resolve(directory, Path.GetFileName(file));
            File.Delete(path);
        }
        Directory.Delete(directory, recursive: false);
    }

    private static string TrimPreviews(string root, string current)
    {
        var diagnostics = new List<string>();
        string[] previous;
        try
        {
            previous = Directory.GetDirectories(root, "plan-*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "旧引脚预览枚举失败：" + ex.Message;
        }
        foreach (var directory in previous)
        {
            if (directory.Equals(current, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(Path.GetFileName(directory), @"\Aplan-[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
            {
                continue;
            }
            try
            {
                var safe = PathBoundary.Resolve(root, Path.GetFileName(directory));
                if (Directory.GetDirectories(safe).Length != 0 || Directory.GetFiles(safe).Any(file => !PreviewFiles.Contains(Path.GetFileName(file))))
                {
                    diagnostics.Add("保留包含其他文件的旧预览：" + Path.GetFileName(safe));
                    continue;
                }
                RemovePreview(safe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException)
            {
                diagnostics.Add("旧引脚预览清理失败：" + ex.Message);
            }
        }
        return string.Join("\n", diagnostics);
    }

    private static string Hash(byte[] source) => Convert.ToHexString(SHA256.HashData(source));
    private static void RequireHash(string actual, string expected)
    {
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("AG32_PIN_PLAN_STALE", "VE 已由其他编辑器修改，请重新加载后再保存图形规划。");
        }
    }

    private sealed record Context(Ag32PinPlanSnapshot Snapshot, Ag32PinPlanDocument Document,
        Ag32PinPlanCatalog Catalog, ResolvedToolset Tools, Ag32PinMappingProjectSettings Settings);
}

namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>仅读取有限的工程/器件元数据；准备、健康检查与构建共用精确需求，不遍历 SDK。</summary>
public static class ProjectDevelopmentComponents
{
    public static void ValidateSnapshot(ProjectManifest project)
    {
        if (project.DevelopmentComponents is not { } needs) return;
        DevelopmentComponentRequirements.Validate(needs);
        if (project.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr)
        {
            if (needs.Count != 0) throw new StudioXException("DEVELOPMENT_COMPONENT_REQUIREMENT", "当前工程类型不使用原生开发环境组件。");
            return;
        }
        if (!needs.Any(item => DevelopmentComponentRequirements.SameIdentity(item, Primary(project))))
            throw new StudioXException("DEVELOPMENT_COMPONENT_REQUIREMENT", "工程开发环境组件需求与主开发环境组件版本不一致，请检查工程配置。");
    }

    public static async Task<IReadOnlyList<DevelopmentComponentRequirement>> ReadAsync(string root, ProjectManifest project, CancellationToken token = default)
    {
        ValidateSnapshot(project);
        if (project.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr) return [];
        IReadOnlyList<DevelopmentComponentRequirement> needs = project.DevelopmentComponents ?? [Primary(project)];
        var devicePath = PathBoundary.Resolve(root, "device/manifest.json");
        if (project.Kind == ProjectKind.Pack && File.Exists(devicePath))
        {
            var pack = await ReadMetadataAsync<PackManifest>(devicePath, 32 * 1024 * 1024, token);
            var device = pack.Devices?.SingleOrDefault(item => item.Id == project.DeviceId);
            var template = device?.Templates?.SingleOrDefault(item => item.Id == project.TemplateId);
            if (device?.DevelopmentComponents is not null || template?.DevelopmentComponents is not null)
            {
                if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || device is null || template is null)
                    throw new StudioXException("DEVELOPMENT_COMPONENT_REQUIREMENT", "器件包开发环境组件声明与工程身份不一致。");
                var declared = DevelopmentComponentRequirements.ForTemplate(device, template);
                if (project.DevelopmentComponents is null) needs = declared;
                else if (needs.Count != declared.Count || declared.Any(item => !needs.Any(n => DevelopmentComponentRequirements.SameIdentity(n, item))))
                    throw new StudioXException("DEVELOPMENT_COMPONENT_REQUIREMENT", "工程开发环境组件快照与器件/模板声明不一致，不能通过删改需求绕过检查。");
            }
        }
        return Configured(project, needs);
    }

    public static IReadOnlyList<DevelopmentComponentRequirement> FromSnapshot(ProjectManifest project)
    {
        ValidateSnapshot(project);
        return project.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr ? []
            : Configured(project, project.DevelopmentComponents ?? [Primary(project)]);
    }

    private static IReadOnlyList<DevelopmentComponentRequirement> Configured(ProjectManifest project, IReadOnlyList<DevelopmentComponentRequirement> needs)
    {
        // 现有特殊功能使用自身已版本化的配置，纳入共同检查但不改写器件包。
        IReadOnlyList<DevelopmentComponentRequirement> mapping = project.PinMapping is { } pin
            ? [new(pin.ToolsetId, pin.ToolsetVersion, pin.CompilerId, Purpose: "引脚映射")] : [];
        IReadOnlyList<DevelopmentComponentRequirement> logic = project.Logic is { } configuredLogic
            ? [new(configuredLogic.ToolsetId, configuredLogic.ToolsetVersion, configuredLogic.CompilerId, Purpose: "已启用的逻辑构建")] : [];
        return DevelopmentComponentRequirements.Merge([Primary(project)], needs, mapping, logic);
    }

    public static async Task<IReadOnlyDictionary<string, string>> ReadPinsAsync(string root,
        IReadOnlyList<DevelopmentComponentRequirement> needs, CancellationToken token = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = PathBoundary.Resolve(root, DevelopmentComponentLock.RelativePath);
        if (File.Exists(path))
        {
            var locked = await ReadMetadataAsync<DevelopmentComponentLock>(path, 1024 * 1024, token);
            if (locked.FormatVersion != 1 || locked.Components is null || locked.Components.Count != needs.Count)
                throw LockError("开发环境组件内容锁与当前需求数量不一致。");
            foreach (var item in locked.Components)
            {
                if (item is null || !needs.Any(n => n.Id == item.Id && n.Version == item.Version && n.Host == item.Host && n.CompilerId == item.CompilerId)
                    || !IsFingerprint(item.Fingerprint) || !result.TryAdd(item.Id, item.Fingerprint))
                    throw LockError("开发环境组件内容锁身份、指纹或重复条目无效。");
            }
        }
        foreach (var relative in new[] { ".studiox/toolchain.lock.json", ".studiox/ag32-mapping-toolchain.lock.json", ".studiox/ag32-logic-toolchain.lock.json" })
        {
            path = PathBoundary.Resolve(root, relative);
            if (!File.Exists(path)) continue;
            var pin = await ReadMetadataAsync<ToolchainLock>(path, 1024 * 1024, token);
            if (relative == ".studiox/ag32-logic-toolchain.lock.json" && pin.ToolsetId == "agm.logic" && pin.Fingerprint is { } combined
                && combined.Split(':') is [var mappingHash, var logicHash] && IsFingerprint(mappingHash) && IsFingerprint(logicHash))
            {
                if (!needs.Any(n => n.Id == "agm.pin-mapping") || !needs.Any(n => n.Id == pin.ToolsetId && n.Version == pin.ToolsetVersion))
                    throw LockError("旧逻辑工具锁与工程组件需求不一致。");
                CheckFingerprint(result, "agm.pin-mapping", mappingHash);
                result["agm.pin-mapping"] = mappingHash;
                pin = pin with { Fingerprint = logicHash };
            }
            if (pin.FormatVersion != 1 || !needs.Any(n => n.Id == pin.ToolsetId && n.Version == pin.ToolsetVersion) || !IsFingerprint(pin.Fingerprint))
                throw LockError("已有工具内容锁与工程需求不一致：" + relative);
            var expectedId = relative switch
            {
                ".studiox/toolchain.lock.json" => needs.FirstOrDefault()?.Id,
                ".studiox/ag32-mapping-toolchain.lock.json" => "agm.pin-mapping",
                _ => "agm.logic"
            };
            if (pin.ToolsetId != expectedId) throw LockError("工具内容锁记录了错误的组件：" + relative);
            if (result.TryGetValue(pin.ToolsetId, out var previous) && !previous.Equals(pin.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw LockError("已有工具锁与开发环境组件内容锁冲突：" + pin.ToolsetId);
            result[pin.ToolsetId] = pin.Fingerprint;
        }
        return result;
    }

    public static void CheckFingerprint(IReadOnlyDictionary<string, string> pins, string id, string fingerprint)
    {
        if (pins.TryGetValue(id, out var pin) && !pin.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            throw LockError("开发环境组件清单与工程锁定内容不同，请恢复相同内容的组件：" + id);
    }

    internal static async Task<T> ReadMetadataAsync<T>(string path, long limit, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (input.Length > limit) throw new StudioXException("DEVELOPMENT_COMPONENT_METADATA", "开发环境组件配置文件过大：" + path);
        using var output = new MemoryStream();
        var buffer = new byte[131072];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new StudioXException("DEVELOPMENT_COMPONENT_METADATA", "开发环境组件配置读取期间超过大小限制：" + path);
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        return System.Text.Json.JsonSerializer.Deserialize<T>(bytes.AsSpan(offset), JsonStore.Options)
            ?? throw new StudioXException("DEVELOPMENT_COMPONENT_METADATA", "开发环境组件配置为空：" + path);
    }

    private static DevelopmentComponentRequirement Primary(ProjectManifest project) => new(project.ToolsetId, project.ToolsetVersion, project.CompilerId);
    private static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static StudioXException LockError(string message) => new("TOOLCHAIN_LOCK", message);
}

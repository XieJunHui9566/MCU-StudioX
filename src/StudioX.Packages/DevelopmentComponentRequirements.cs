namespace StudioX.Packages;

using StudioX.Foundation;

public static class DevelopmentComponentRequirements
{
    public static void Validate(IReadOnlyList<DevelopmentComponentRequirement> requirements)
    {
        if (requirements.Count > 64) throw Invalid("开发环境组件需求超过 64 项。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in requirements)
        {
            if (item is null) throw Invalid("开发环境组件需求不能为空。");
            PackValidator.Token(item.Id); PackValidator.Version(item.Version);
            if (!ids.Add(item.Id)) throw Invalid("同一层不能重复声明开发环境组件：" + item.Id);
            if (item.Host != "win-x64") throw Invalid("当前仅支持 win-x64 开发环境组件：" + item.Id);
            if (string.IsNullOrWhiteSpace(item.CompilerId) || item.CompilerId.Length > 128 || item.CompilerId.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(item.Purpose) || item.Purpose.Length > 160 || item.Purpose.Any(char.IsControl))
                throw Invalid("开发环境组件缺少有效的编译器身份或用途：" + item.Id);
        }
    }

    public static bool SameIdentity(DevelopmentComponentRequirement left, DevelopmentComponentRequirement right)
        => left.Id == right.Id && left.Version == right.Version && left.CompilerId == right.CompilerId && left.Host == right.Host;

    public static IReadOnlyList<DevelopmentComponentRequirement> Merge(params IReadOnlyList<DevelopmentComponentRequirement>[] groups)
    {
        var result = new List<DevelopmentComponentRequirement>();
        foreach (var group in groups)
        {
            Validate(group);
            foreach (var item in group)
            {
                var previous = result.SingleOrDefault(r => r.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
                if (previous is null) result.Add(item);
                else if (!SameIdentity(previous, item)) throw Invalid("开发环境组件的版本、平台或编译器声明冲突：" + item.Id);
            }
        }
        Validate(result);
        return result.ToArray();
    }

    public static IReadOnlyList<DevelopmentComponentRequirement> ForTemplate(DeviceDefinition device, ProjectTemplate template)
    {
        // 格式 1 已发布包的三个工具字段本身就是明确需求，不按芯片或本机安装情况推断版本。
        DevelopmentComponentRequirement primary = new(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
        var deviceNeeds = device.DevelopmentComponents ?? [primary];
        Validate(deviceNeeds); Validate(template.DevelopmentComponents ?? []);
        if (!deviceNeeds.Any(item => SameIdentity(item, primary)))
            throw Invalid("器件开发环境组件必须包含与主工具字段一致的精确版本：" + device.ToolsetId);
        if (template.MicroPython is not null)
        {
            if (template.DevelopmentComponents is { Count: > 0 }) throw Invalid("MicroPython 脚本模板不能声明原生构建组件。");
            return [];
        }
        return Merge(deviceNeeds, template.DevelopmentComponents ?? []);
    }

    private static StudioXException Invalid(string message) => new("DEVELOPMENT_COMPONENT_REQUIREMENT", message);
}

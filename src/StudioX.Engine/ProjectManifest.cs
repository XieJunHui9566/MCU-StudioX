namespace StudioX.Engine;

public sealed record ProjectManifest(int FormatVersion, string Name, string PackId, string PackVersion, string PackContentHash,
    string DeviceId, string TemplateId, string ToolsetId, string ToolsetVersion, string CompilerId,
    ProjectKind Kind = ProjectKind.Pack, CubeMxProjectSettings? CubeMx = null, Ag32LogicProjectSettings? Logic = null,
    EspressifProjectSettings? Espressif = null, string? EntryFile = null, Ag32PinMappingProjectSettings? PinMapping = null,
    ZephyrProjectSettings? Zephyr = null, StudioX.Packages.MicroPythonProfile? MicroPython = null,
    IReadOnlyList<StudioX.Packages.DevelopmentComponentRequirement>? DevelopmentComponents = null)
{
    // 构建凭据会重新反序列化工程；数组引用相等会让有效 ELF 永久被误判为配置已变化。
    public bool Equals(ProjectManifest? other) => other is not null &&
        FormatVersion == other.FormatVersion && Name == other.Name && PackId == other.PackId && PackVersion == other.PackVersion &&
        PackContentHash == other.PackContentHash && DeviceId == other.DeviceId && TemplateId == other.TemplateId &&
        ToolsetId == other.ToolsetId && ToolsetVersion == other.ToolsetVersion && CompilerId == other.CompilerId && Kind == other.Kind &&
        CubeMx == other.CubeMx && Logic == other.Logic && Espressif == other.Espressif && EntryFile == other.EntryFile &&
        PinMapping == other.PinMapping && Zephyr == other.Zephyr && MicroPython == other.MicroPython &&
        (DevelopmentComponents is null ? other.DevelopmentComponents is null
            : other.DevelopmentComponents is not null && DevelopmentComponents.SequenceEqual(other.DevelopmentComponents));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FormatVersion); hash.Add(Name); hash.Add(PackId); hash.Add(PackVersion); hash.Add(PackContentHash);
        hash.Add(DeviceId); hash.Add(TemplateId); hash.Add(ToolsetId); hash.Add(ToolsetVersion); hash.Add(CompilerId); hash.Add(Kind);
        hash.Add(CubeMx); hash.Add(Logic); hash.Add(Espressif); hash.Add(EntryFile); hash.Add(PinMapping); hash.Add(Zephyr); hash.Add(MicroPython);
        hash.Add(DevelopmentComponents is not null);
        foreach (var item in DevelopmentComponents ?? []) hash.Add(item);
        return hash.ToHashCode();
    }
}

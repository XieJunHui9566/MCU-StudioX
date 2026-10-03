namespace StudioX.Packages;

/// <summary>普通模板入口复制到 src；原生 SDK 示例保留厂商布局，公共设备资源仍归芯片包。</summary>
public sealed record ProjectTemplate(string Id, string DisplayName, string Description, string EntryFile,
    TemplateBuild? Build = null, IReadOnlyDictionary<string, string>? Files = null, EspressifExampleTemplate? EspressifExample = null,
    MicroPythonProfile? MicroPython = null, Ag32LogicTemplate? Ag32Sources = null,
    IReadOnlyList<string>? ReplacesTemplates = null,
    IReadOnlyList<DevelopmentComponentRequirement>? DevelopmentComponents = null);

namespace StudioX.Packages;

/// <summary>模板叠加到器件的构建参数；HAL、SPL 和 RTOS 只启用所选组合。</summary>
public sealed record TemplateBuild(IReadOnlyList<string> Defines, IReadOnlyList<string> IncludeDirectories,
    IReadOnlyList<string> Sources, IReadOnlyList<string> CompileOptions, IReadOnlyList<string> LinkOptions);

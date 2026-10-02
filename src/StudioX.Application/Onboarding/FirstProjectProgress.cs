namespace StudioX.Application.Onboarding;

/// <summary>当前工作区的操作证据；切换工程后重新收集，不把教程勾选当作编译验收。</summary>
public sealed record FirstProjectProgress(
    string? ProjectDirectory = null, string ProjectName = "", string Target = "", string Toolset = "",
    bool Script = false, bool Experimental = false, bool Espressif = false,
    bool HealthPassed = false, bool SourceSaved = false, bool? BuildSucceeded = null,
    IReadOnlyList<string>? Artifacts = null);

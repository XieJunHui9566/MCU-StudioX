namespace StudioX.Engine;

/// <summary>具有官方来源的完整模组规格；设置仍须与工程的准确 SDK 目标匹配。</summary>
public sealed record EspressifModuleProfile(string Id, string Label, string Target,
    EspressifModuleSettings Settings, string Summary, string SourceUrl);

namespace StudioX.Engine.Lvgl;

/// <summary>只核对已有目标构建证据；PC 预览不能替代目标固件编译。</summary>
public sealed record LvglTargetBuildEvidence(string State, bool MatchesCurrentUi, string Message,
    IReadOnlyList<string> MissingUiSources);

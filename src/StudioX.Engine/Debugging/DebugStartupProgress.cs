namespace StudioX.Engine.Debugging;

/// <summary>工具实际进展；未给出百分比的阶段不估算完成比例。</summary>
public sealed record DebugStartupProgress(string Stage, string Message, bool Completed = false);

namespace StudioX.Engine.Debugging;

public sealed record CoreDumpTask(string Name, uint Tcb, uint? StackStart, uint StackBytes, uint? Flags);
public sealed record CoreDumpEvidence(string DumpSha256, string Format, string Target, string ToolsetVersion,
    string DecoderVersion, string? EmbeddedElfHash, bool HashMatches, IReadOnlyList<CoreDumpTask> Tasks,
    string? CrashedTask, string? PanicDetails, string ElfSource);

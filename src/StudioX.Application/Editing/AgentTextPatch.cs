namespace StudioX.Application.Editing;

public sealed record AgentTextPatch(string Path, string ContentHash, IReadOnlyList<AgentTextHunk> Hunks);

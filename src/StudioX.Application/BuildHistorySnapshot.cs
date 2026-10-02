namespace StudioX.Application;

using StudioX.Engine;

public sealed record BuildHistorySnapshot(string Id, DateTimeOffset RecordedAtUtc, BuildDetails Details,
    BuildMemoryReport Memory, double? BuildSeconds)
{
    public string DisplayName => $"{RecordedAtUtc.ToLocalTime():MM-dd HH:mm:ss} · {Details.Project.Name} · {Details.SourceStamp[..Math.Min(8, Details.SourceStamp.Length)]}";
}

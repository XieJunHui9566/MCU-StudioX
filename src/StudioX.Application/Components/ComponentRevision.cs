namespace StudioX.Application.Components;

public sealed record ComponentRevision(DateTimeOffset RecordedAtUtc, InstalledComponent[] Components);

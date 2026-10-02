namespace StudioX.Application.Components;

public sealed record ComponentLock(int FormatVersion, string Framework, string Target, InstalledComponent[] Components,
    Dictionary<string, string> GeneratedHashes, ComponentRevision[] History);

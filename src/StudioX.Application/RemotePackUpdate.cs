namespace StudioX.Application;

public sealed record RemotePackUpdate(string Id, string Version, string? InstalledVersion, string Path);

namespace StudioX.Engine;

using StudioX.Packages;
public sealed record DownloadConfiguration(DeviceDefinition Device, OpenOcdDefinition OpenOcd, DownloadOptions Options, string? TargetScriptText = null);

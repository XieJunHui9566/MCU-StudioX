namespace StudioX.Engine;

using StudioX.Packages;

public sealed record EspressifFlashConfiguration(ProjectManifest Project, DeviceDefinition Device,
    EspressifFlashSettings Settings);

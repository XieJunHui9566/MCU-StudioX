namespace StudioX.Engine;

/// <summary>电脑上的示教平台模型能力，与器件的实物在线调试能力分开。</summary>
public static class TeachingSimulatorTarget
{
    public const string DeviceId = "STC89C52RC";
    public const int CodeSize = 8192;
    public static bool Supports(ProjectManifest? project) => project is { ToolsetId: "stc.sdcc", DeviceId: DeviceId };
}

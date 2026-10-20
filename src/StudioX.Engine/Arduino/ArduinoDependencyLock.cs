namespace StudioX.Engine.Arduino;

/// <summary>锁定工具及完整库源码内容，名称和版本相同但内容改变时仍拒绝复用。</summary>
public sealed record ArduinoDependencyLock(int FormatVersion, string ToolFingerprint, string LibrariesSha256)
{
    public const string RelativePath = ".studiox/arduino.lock.json";
}

namespace StudioX.Engine.Arduino;

/// <summary>只有源码、依赖和镜像全部一致的成功编译才可用于上传。</summary>
public sealed record ArduinoBuildReceipt(int FormatVersion, string SourceSha256, string ToolFingerprint,
    string LibrariesSha256, string HexRelativePath, string HexSha256, IReadOnlyDictionary<string, string>? UploadFiles = null)
{
    public const string RelativePath = ".studiox/arduino-build.json";
}

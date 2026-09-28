namespace StudioX.Engine;

internal sealed record Ag32PinMappingReceipt(int FormatVersion, Ag32PinMappingProjectSettings Settings,
    string SourceSha256, string ToolFingerprint, string ImageSha256, long ImageBytes,
    string? VexSha256 = null, string? IoAsfSha256 = null, string? RoutedSha256 = null, string? VxSha256 = null,
    string? HeaderSha256 = null, string? SdcSha256 = null, string? TimingSha256 = null)
{
    internal const string RelativePath = ".build/ag32-mapping/studiox-mapping-receipt.json";
    internal const string ImageRelativePath = ".build/ag32-mapping/pins.bin";
    internal const string LockRelativePath = ".studiox/ag32-mapping-toolchain.lock.json";
}

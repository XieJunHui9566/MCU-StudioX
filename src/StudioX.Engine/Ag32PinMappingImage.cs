namespace StudioX.Engine;

/// <summary>与当前 VE、明确器件和已锁定工具匹配的独立逻辑镜像。</summary>
public sealed record Ag32PinMappingImage(string Path, string Sha256, long ByteCount,
    string SourceSha256, string ToolFingerprint, uint FlashAddress = 0x80027000);

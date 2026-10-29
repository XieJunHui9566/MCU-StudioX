namespace StudioX.Engine.Pic;

public sealed record PicFirmwarePreview(string DeviceId, string HexPath, string Sha256, long Bytes, string ElfPath);

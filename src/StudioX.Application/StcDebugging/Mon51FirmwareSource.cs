namespace StudioX.Application.StcDebugging;

/// <summary>用户选择的完整包身份；不携带可由界面指定的磁盘路径。</summary>
public sealed record Mon51FirmwareSource(string PackId, string PackVersion, string PackContentHash,
    string DeviceId, string FirmwareId, string FirmwareVersion, string ImageSha256, bool IsProjectPack)
{
    public string DisplayName => $"Mon51 {FirmwareVersion} · {PackId} / {PackVersion}";
}

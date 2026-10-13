namespace StudioX.Packages;

/// <summary>器件包提供的监控镜像及适用引导程序；安装包完整性不能代替设备身份核对。</summary>
public sealed record MonitorFirmwareDefinition(string Id, string DisplayName, string Version, string Protocol,
    string ImageFile, string ImageSha256, int ImageBytes, string ProvenanceFile,
    string BootloaderVersion, int BootloaderStatus);

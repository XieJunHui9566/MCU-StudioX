namespace StudioX.Engine.Pic;

/// <summary>绑定构建凭据、配置字、精确烧录组件及 PICkit3 序列号；预检不连接目标。</summary>
public sealed record PicDownloadPreview(PicFirmwarePreview Firmware, PicDownloadSettings Settings,
    PicHexImageInfo Image, string ToolDisplayName, string ApprovalId);

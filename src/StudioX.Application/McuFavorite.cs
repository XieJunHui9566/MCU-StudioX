namespace StudioX.Application;

/// <summary>收藏准确的器件包和型号身份，不将用户偏好绑定到包版本或工程模板。</summary>
public sealed record McuFavorite(string PackId, string DeviceId, string DisplayName, string PackDisplayName, string Vendor);

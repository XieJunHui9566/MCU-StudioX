namespace StudioX.Engine.Arduino;

/// <summary>上传前展示的准确板卡及固件身份；准备阶段不访问设备。</summary>
public sealed record ArduinoUploadPreview(ArduinoBoard Board, string HexPath, string Sha256, long Bytes, string ApprovalId);

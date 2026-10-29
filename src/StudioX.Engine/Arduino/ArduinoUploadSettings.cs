namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>保存当前工程明确选择的上传端口；设置与源码、构建凭据分别存储。</summary>
public sealed record ArduinoUploadSettings(int FormatVersion = 1, string Port = "")
{
    public const string RelativePath = ".studiox/arduino-upload.json";

    public void Validate()
    {
        if (FormatVersion != 1 || Port is null)
        {
            throw new StudioXException("ARDUINO_UPLOAD_SETTINGS", "Arduino 上传设置格式无效。");
        }
        if (Port.Length > 0 && ArduinoBuildService.ValidatePort(Port) != Port)
        {
            throw new StudioXException("ARDUINO_UPLOAD_SETTINGS", "Arduino 上传端口需要保存为规范的 COM 名称。");
        }
    }
}

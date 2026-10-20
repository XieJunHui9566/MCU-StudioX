namespace StudioX.Engine.Arduino;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>Arduino 库索引中的名称和精确版本，不能携带命令行选项或下载地址。</summary>
public sealed record ArduinoLibrary(string Name, string Version)
{
    public void Validate()
    {
        if (!Regex.IsMatch(Name ?? "", @"\A[A-Za-z0-9][A-Za-z0-9 ._+()\-]{0,119}\z") ||
            !Regex.IsMatch(Version ?? "", @"\A\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.\-]+)?\z"))
        {
            throw new StudioXException("ARDUINO_LIBRARY", "库需要索引中的完整名称和精确版本，例如 Adafruit AHTX0 / 2.0.5。");
        }
    }
}

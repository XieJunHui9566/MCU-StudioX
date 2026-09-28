namespace StudioX.Packages;

using System.Text.Json.Serialization;
using StudioX.Foundation;

/// <summary>明确板型和解释器版本；C SDK 的工具集与下载配置不适用于脚本模板。</summary>
public sealed record MicroPythonProfile(string Board, string Version)
{
    [JsonIgnore]
    public string FirmwarePage => "https://micropython.org/download/" + Board + "/";
    [JsonIgnore]
    public string ExpectedMachine => Board switch
    {
        "RPI_PICO" => "Raspberry Pi Pico with RP2040",
        "RPI_PICO2" => "Raspberry Pi Pico2 with RP2350",
        _ => throw new StudioXException("MICROPYTHON_BOARD", "不支持的 MicroPython 板型。")
    };

    public void Validate(string deviceId)
    {
        if (Version != "1.29.0" || !((Board == "RPI_PICO" && deviceId == "RP2040-PICO") ||
            (Board == "RPI_PICO2" && deviceId == "RP2350A-PICO2")))
        {
            throw new StudioXException("MICROPYTHON_PROFILE", "需要明确的 Pico / Pico 2 板型和 MicroPython 1.29.0 配置。");
        }
    }
}

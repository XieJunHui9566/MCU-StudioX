namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>框架与芯片分开建模；不同 Arduino 核心使用独立、精确版本的开发组件。</summary>
public sealed record ArduinoPlatform(string Id, string CoreId, string CoreVersion, string ToolsetId, string CompilerId, string DisplayName)
{
    public const string CliVersion = "1.5.1";
    public static ArduinoPlatform Avr { get; } = new("avr", "arduino:avr", "1.8.8", "arduino.avr", "avr-gcc-7.3.0-arduino7", "Arduino AVR Boards 1.8.8");
    public static ArduinoPlatform Esp32 { get; } = new("esp32", "esp32:esp32", "3.3.8", "arduino.esp32", "esp-gcc-14.2.0-2601", "Arduino ESP32 3.3.8");
    public static ArduinoPlatform Require(string id) => id switch
    {
        "avr" => Avr,
        "esp32" => Esp32,
        _ => throw new StudioXException("ARDUINO_PLATFORM", "未适配的 Arduino 核心：" + id)
    };
}

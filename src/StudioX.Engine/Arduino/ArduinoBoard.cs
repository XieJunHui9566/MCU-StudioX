namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>经过固定官方核心板卡定义核对的目标；不根据芯片名称或串口猜测板型。</summary>
public sealed record ArduinoBoard(string Id, string DisplayName, string Fqbn, int FlashBytes, int RamBytes,
    string PlatformId = "avr", string DeviceId = "ATmega328P", string? Target = null)
{
    public ArduinoPlatform Platform => ArduinoPlatform.Require(PlatformId);
    public string FirmwareSuffix => PlatformId == "avr" ? ".ino.hex" : ".ino.bin";
    public static IReadOnlyList<ArduinoBoard> Supported
    {
        get;
    } =
    [
        new("uno", "Arduino UNO R3 · ATmega328P", "arduino:avr:uno", 32256, 2048),
        new("nano", "经典 Nano · ATmega328P · 新引导程序", "arduino:avr:nano:cpu=atmega328", 30720, 2048),
        new("nano-old", "经典 Nano · ATmega328P · 旧引导程序", "arduino:avr:nano:cpu=atmega328old", 30720, 2048),
        new("esp32", "ESP32 Dev Module · 4 MB Flash · 无 PSRAM", "esp32:esp32:esp32:FlashSize=4M,PSRAM=disabled,EraseFlash=none", 0, 0, "esp32", "ESP32-WROOM-32", "esp32"),
        new("esp32c3", "ESP32-C3 Dev Module · 4 MB Flash", "esp32:esp32:esp32c3:FlashSize=4M,EraseFlash=none", 0, 0, "esp32", "ESP32-C3", "esp32c3"),
        new("esp32c5", "ESP32-C5 Dev Module · 4 MB Flash · 无 PSRAM", "esp32:esp32:esp32c5:FlashSize=4M,PSRAM=disabled,EraseFlash=none", 0, 0, "esp32", "ESP32-C5", "esp32c5"),
        new("esp32c6", "ESP32-C6 Dev Module · 4 MB Flash", "esp32:esp32:esp32c6:FlashSize=4M,EraseFlash=none", 0, 0, "esp32", "ESP32-C6", "esp32c6"),
        new("esp32s3", "ESP32-S3 Dev Module · 4 MB Flash · 无 PSRAM", "esp32:esp32:esp32s3:FlashSize=4M,PSRAM=disabled,EraseFlash=none", 0, 0, "esp32", "ESP32-S3", "esp32s3")
    ];

    public static ArduinoBoard Require(string id) => Supported.SingleOrDefault(board => board.Id == id)
        ?? throw new StudioXException("ARDUINO_BOARD", "请选择已适配的准确 Arduino 开发板配置。");

    public static IReadOnlyList<ArduinoBoard> ForDevice(StudioX.Packages.DeviceDefinition device) => Supported.Where(board =>
        board.DeviceId == device.Id && (board.PlatformId == "avr" ? device.Architecture == "avr" :
            device.Espressif?.Framework == "esp-idf" && device.Espressif.Target == board.Target)).ToArray();
}

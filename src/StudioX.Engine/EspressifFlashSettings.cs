namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>串口选项只存储用户选择；打开工程、列举端口或构建不会连接硬件。</summary>
public sealed record EspressifFlashSettings(int FormatVersion = 1, string Port = "", int BaudRate = 460800)
{
    public const string RelativePath = ".studiox/espressif-flash.json";

    public void Validate(bool requirePort = false)
    {
        if (FormatVersion != 1 || BaudRate is not (115200 or 230400 or 460800 or 921600) ||
            Port.Length > 8 || Port.Length > 0 && !Regex.IsMatch(Port, @"^COM[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant) ||
            requirePort && Port.Length == 0)
        {
            throw new StudioXException("ESP_FLASH_SETTINGS", "请选择明确的 COM 端口和 115200、230400、460800 或 921600 baud。");
        }
    }

    public static async Task<EspressifFlashSettings> ReadAsync(string root, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(root, RelativePath);
        var settings = File.Exists(path) ? await JsonStore.ReadAsync<EspressifFlashSettings>(path, token) : new();
        settings.Validate();
        return settings;
    }
}

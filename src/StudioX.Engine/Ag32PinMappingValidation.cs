namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

internal static class Ag32PinMappingValidation
{
    internal static async Task<byte[]> ReadSourceAsync(string root, Ag32PinMappingProjectSettings settings,
        IReadOnlySet<int> assignablePins, Ag32DeviceProfile profile, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, settings.PinMapFile);
        if (!File.Exists(path))
        {
            throw new StudioXException("AG32_MAPPING_SOURCE", "缺少引脚映射源文件：" + settings.PinMapFile);
        }
        if (new FileInfo(path).Length > 256 * 1024)
        {
            throw new StudioXException("AG32_MAPPING_SOURCE", "基础 VE 文件超过 256 KiB，无法作为引脚配置处理。");
        }
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > 256 * 1024)
        {
            throw new StudioXException("AG32_MAPPING_SOURCE", "读取期间 VE 文件超过大小限制。");
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new StudioXException("AG32_MAPPING_ENCODING", "VE 文件需要有效 UTF-8 编码。", ex);
        }
        var lineNumber = 0;
        var hasMapping = false;
        var declaredClocks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Split('#', 2)[0];
            // 厂商 VE 允许空格、制表符和逗号分词；不能用 SYSCLK,1000 绕过数据手册频率限制。
            var words = Regex.Split(line.TrimStart((char)0xfeff).Trim(), @"[\s,]+", RegexOptions.CultureInvariant);
            if (words.Length != 0 && words[0] is "HSECLK" or "SYSCLK" or "BUSCLK")
            {
                if (words.Length < 2 || !declaredClocks.Add(words[0]) ||
                    !decimal.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var frequency) ||
                    frequency < 0 || words[0] == "SYSCLK" && frequency == 0 ||
                    words[0] is "SYSCLK" or "BUSCLK" && frequency > profile.MaximumSysClockMhz)
                {
                    throw new StudioXException("AG32_MAPPING_CLOCK",
                        $"{settings.PinMapFile}:{lineNumber}：{words[0]} 配置无效、重复或超过 {profile.MaximumSysClockMhz} MHz 的器件系统/总线限制。");
                }
            }
            if (Regex.IsMatch(line.TrimStart((char)0xfeff), @"^\s*(?:ASSIGN\b|@)", RegexOptions.CultureInvariant))
            {
                throw new StudioXException("AG32_MAPPING_CUSTOM_LOGIC", $"{settings.PinMapFile}:{lineNumber}：基础映射不执行自定义 ASSIGN 逻辑，请使用独立 Verilog 流程。");
            }
            foreach (Match match in Regex.Matches(line, @"\bPIN_([0-9]+)\b", RegexOptions.CultureInvariant))
            {
                if (!int.TryParse(match.Groups[1].Value, out var pin) || !assignablePins.Contains(pin))
                {
                    throw new StudioXException("AG32_MAPPING_PIN", $"{settings.PinMapFile}:{lineNumber}：{match.Value} 不是 {settings.TargetDevice} 可自由映射的封装引脚。");
                }
                hasMapping = true;
            }
        }
        if (!hasMapping && !Ag32PeripheralSupport.Read(bytes).Enabled)
        {
            throw new StudioXException("AG32_MAPPING_EMPTY", "请先按真实接线填写 VE 封装引脚映射；只有注释或时钟配置的骨架不能作为可下载映射。");
        }
        return bytes;
    }
}

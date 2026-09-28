namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>网表中的实例名字不能证明封装布线；必须核对布局器输出的真实 IO 分配。</summary>
internal sealed record Ag32PinMappingRouting(string VexSha256, string IoAsfSha256, string RoutedSha256, string VxSha256,
    string HeaderSha256, string SdcSha256, string[] Mappings)
{
    internal static async Task<Ag32PinMappingRouting> VerifyAsync(string build, byte[] source, CancellationToken token)
    {
        var vex = await ReadAsync("pins.vex");
        var io = await ReadAsync("logic_db/io.asf");
        var routed = await ReadAsync("pins_routed.v");
        var netlist = await ReadAsync("pins.vx");
        var header = await ReadAsync("pins.hx");
        var sdc = await ReadAsync("studiox-clocks.sdc");
        var expected = Regex.Matches(vex.Text, @"^\s*(\S+)\s+(PIN_[0-9]+)(?:\s|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => (Port: match.Groups[1].Value, Pin: match.Groups[2].Value)).ToArray();
        if (expected.Length == 0)
        {
            throw new StudioXException("AG32_MAPPING_ROUTING", "转换器没有生成实际 GPIO 到封装引脚的 VEX 约束。");
        }
        var requested = Regex.Matches(Encoding.UTF8.GetString(source).TrimStart((char)0xfeff), @"^\s*([^#\s]+)\s+(PIN_[0-9]+)(?=[:\s]|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => (Function: match.Groups[1].Value, Pin: match.Groups[2].Value)).ToArray();
        var requestedPins = requested.Select(mapping => mapping.Pin).Distinct(StringComparer.Ordinal).ToArray();
        if (requestedPins.Any(pin => !expected.Any(mapping => mapping.Pin == pin)))
        {
            throw new StudioXException("AG32_MAPPING_ROUTING", "VE 中的物理映射没有完整转换为 VEX 约束。");
        }
        foreach (var (function, pin) in requested)
        {
            var port = expected.SingleOrDefault(mapping => mapping.Pin == pin).Port;
            var block = Regex.Match(netlist.Text, @"// Location:\s*" + Regex.Escape(pin) + @"\s*\r?\n(?<block>.*?\.padio\s*\(\s*"
                + Regex.Escape(port ?? "") + @"\s*\)\s*\);\s*//\s*(?<functions>[^\r\n]*))", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!block.Success || !block.Groups["functions"].Value.Split(',').Select(value => value.Trim()).Contains(function, StringComparer.Ordinal))
            {
                throw new StudioXException("AG32_MAPPING_ROUTING", $"生成的 IO 网表未保留请求功能 {function} → {pin}。");
            }
        }
        var actual = Regex.Matches(io.Text, @"^\s*set_location_assignment\s+-to\s+(\S+)\s+(PIN_[0-9]+)(?:\s|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => (Port: match.Groups[1].Value, Pin: match.Groups[2].Value)).ToArray();
        foreach (var (port, pin) in expected)
        {
            if (actual.Count(mapping => mapping.Port == port && mapping.Pin == pin) != 1 ||
                actual.Any(mapping => mapping.Port == port && mapping.Pin != pin) ||
                !Regex.IsMatch(routed.Text, @"\.padio\s*\(\s*" + Regex.Escape(port) + @"\s*\)", RegexOptions.CultureInvariant))
            {
                throw new StudioXException("AG32_MAPPING_ROUTING", $"Supra 实际 IO 布线没有匹配请求的 {port} → {pin}；禁止生成可下载凭据。");
            }
        }
        if (actual.Any(mapping => !expected.Contains(mapping)))
        {
            throw new StudioXException("AG32_MAPPING_ROUTING", "布局器生成了未经 VEX 约束的额外封装 GPIO 分配。");
        }
        var clocks = await Ag32PinMappingClockVerification.VerifyAsync(build, token);
        return new(vex.Hash, io.Hash, routed.Hash, netlist.Hash, header.Hash, sdc.Hash,
            expected.Select(mapping => mapping.Port + " → " + mapping.Pin).Concat(clocks).ToArray());

        async Task<(string Text, string Hash)> ReadAsync(string relative)
        {
            var path = PathBoundary.Resolve(build, relative);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 8 * 1024 * 1024)
            {
                throw new StudioXException("AG32_MAPPING_ROUTING", "缺少有效的物理引脚分配证据：" + relative);
            }
            var bytes = await File.ReadAllBytesAsync(path, token);
            if (bytes.Length > 8 * 1024 * 1024)
            {
                throw new StudioXException("AG32_MAPPING_ROUTING", "物理布线证据文件超过大小限制。");
            }
            return (Encoding.UTF8.GetString(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
}

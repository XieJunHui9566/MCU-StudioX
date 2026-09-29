namespace StudioX.MakerPlugins;

using System.Globalization;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;
using static StudioX.LabPlugins.LabPanel;

public sealed class RgbStudioPlugin : LabPlugin
{
    public override string Title => "RGB 灯效工坊";
    protected override string Introduction => "制作彩虹、双色渐变或呼吸灯帧表，查看三路亮度曲线，复制 RGB / GRB 与 RGB565 数据。参数改变后点击计算。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["mode"] = EnumSchema("rainbow", "gradient", "breathe"), ["count"] = NumberSchema(2, 32, true),
        ["start"] = StringSchema(7), ["end"] = StringSchema(7), ["brightness"] = NumberSchema(0, 100),
        ["gamma"] = NumberSchema(0.1, 5), ["order"] = EnumSchema("rgb", "grb")
    });
    protected override PluginPanelWidget[] Inputs(JsonElement v) =>
    [
        Select("mode", "灯效", LabInput.Text(v, "mode", "rainbow"), ("rainbow", "彩虹循环"), ("gradient", "双色渐变"), ("breathe", "单色呼吸")),
        Number("count", "帧数 / 颜色数（2–32）", LabInput.Integer(v, "count", 16, 2, 32)),
        Input("start", "起始色 #RRGGBB（彩虹忽略）", LabInput.Text(v, "start", "#FF4000")),
        Input("end", "结束色 #RRGGBB（仅渐变）", LabInput.Text(v, "end", "#0055FF")),
        Number("brightness", "总亮度 / %", LabInput.Number(v, "brightness", 50, 0, 100)),
        Number("gamma", "亮度指数 γ（1 为线性）", LabInput.Number(v, "gamma", 1, 0.1, 5)),
        Select("order", "三字节数组顺序", LabInput.Text(v, "order", "rgb"), ("rgb", "RGB"), ("grb", "GRB"))
    ];

    public override LabResult Calculate(JsonElement v)
    {
        var mode = LabInput.Text(v, "mode", "rainbow");
        var order = LabInput.Text(v, "order", "rgb");
        if (mode is not ("rainbow" or "gradient" or "breathe") || order is not ("rgb" or "grb"))
            throw new ArgumentException("请选择支持的灯效和字节顺序。");
        var count = LabInput.Integer(v, "count", 16, 2, 32);
        var brightness = LabInput.Number(v, "brightness", 50, 0, 100) / 100;
        var gamma = LabInput.Number(v, "gamma", 1, 0.1, 5);
        var start = mode == "rainbow" ? new double[3] : Color(LabInput.Text(v, "start", "#FF4000", 7));
        var end = mode == "gradient" ? Color(LabInput.Text(v, "end", "#0055FF", 7)) : start;
        var frames = Enumerable.Range(0, count).Select(i =>
        {
            var source = mode switch
            {
                "rainbow" => Hue(6d * i / count),
                "gradient" => start.Select((c, k) => c + (end[k] - c) * i / (count - 1)).ToArray(),
                _ => start.Select(c => c * (1 - Math.Cos(2 * Math.PI * i / count)) / 2).ToArray()
            };
            // 消除三角函数在精确半码边界附近的浮点尾差，保证对称帧得到相同量化值。
            return source.Select(c => (int)Math.Clamp(Math.Round(255 * brightness * Math.Pow(Math.Clamp(c, 0, 1), gamma) + 1e-10, MidpointRounding.AwayFromZero), 0, 255)).ToArray();
        }).ToArray();
        var bytes = frames.Select(c => order == "rgb" ? c : new[] { c[1], c[0], c[2] }).ToArray();
        var rgb565 = frames.Select(c => (c[0] >> 3) << 11 | (c[1] >> 2) << 5 | c[2] >> 3).ToArray();
        var code = "#include <stdint.h>\n/* " + mode + "; " + order.ToUpperInvariant() + "; gamma=" + gamma.ToString("G9", CultureInfo.InvariantCulture) + " */\n" +
            $"static const uint8_t led_frames[{count}][3] = {{\n" + string.Join("\n", bytes.Select(c => "    {" + string.Join(", ", c) + "},")) + "\n};\n" +
            "/* Logical RGB565 words; select byte order in your display driver. */\n" + $"static const uint16_t palette565[{count}] = {{\n    " + string.Join(", ", rgb565.Select(c => $"0x{c:X4}")) + "\n};\n";
        var widgets = new List<PluginPanelWidget>
        {
            Text("summary", "生成结果", $"{count} 帧；数组顺序 {order.ToUpperInvariant()}；RGB565 是 R5:G6:B5 数值，不指定传输字节序。"),
            Text("formula", "亮度规则", "输出 = round(255 × 总亮度 × 分量^γ)。循环不重复末点；渐变包含两端。这里只生成数据，由工程的驱动按需播放。")
        };
        for (var channel = 0; channel < 3; channel++)
        {
            var k = channel;
            widgets.Add(new("channel" + k, "plot", "RGB"[k] + " 分量：X=帧序号，Y=代码 0–255", Json(frames.Select((c, i) => new { x = i, y = c[k] }))));
        }
        widgets.Add(Table("colors", "帧明细", ["序号", "输出 #RRGGBB", "RGB565"], frames.Select((c, i) => new[] { i.ToString(CultureInfo.InvariantCulture), $"#{c[0]:X2}{c[1]:X2}{c[2]:X2}", $"0x{rgb565[i]:X4}" })));
        return new(new { mode, order, frames, bytes, rgb565 }, code, widgets.ToArray());
    }

    private static double[] Color(string text)
    {
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("颜色应为 #RRGGBB，例如 #FF4000。");
        return new[] { (value >> 16 & 255) / 255d, (value >> 8 & 255) / 255d, (value & 255) / 255d };
    }

    private static double[] Hue(double h)
    {
        var x = 1 - Math.Abs(h % 2 - 1);
        return (int)h switch { 0 => [1, x, 0], 1 => [x, 1, 0], 2 => [0, 1, x], 3 => [0, x, 1], 4 => [x, 0, 1], _ => [1, 0, x] };
    }
}

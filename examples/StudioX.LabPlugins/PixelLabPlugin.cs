namespace StudioX.LabPlugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using static LabPanel;

public sealed class PixelLabPlugin : LabPlugin
{
    private const string Heart = "01100110/11111111/11111111/11111111/01111110/00111100/00011000/00000000";
    private const string Smile = "00111100/01000010/10100101/10000001/10100101/10011001/01000010/00111100";
    private const string Alien = "00100100/00011000/00111100/01111110/11011011/11111111/10100101/00100100";
    public override string Title => "像素图案工坊";
    protected override string Introduction => "从爱心、笑脸、小怪兽开始，或输入 0/1 像素行（用 / 分隔）。生成 1 位点阵字节，支持水平翻转与反色。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["pattern"] = EnumSchema("heart", "smile", "alien", "custom"),
        ["pixels"] = StringSchema(1200),
        ["packing"] = EnumSchema("row-msb", "page-lsb"),
        ["flip"] = new
        {
            type = "boolean"
        },
        ["invert"] = new
        {
            type = "boolean"
        }
    });
    protected override PluginPanelWidget[] Inputs(JsonElement values) =>
    [
        Select("pattern", "图案", LabInput.Text(values, "pattern", "heart"), ("heart", "爱心 ♥"), ("smile", "笑脸"), ("alien", "小怪兽"), ("custom", "自定义")),
        Input("pixels", "自定义像素行（仅自定义模式使用；宽高 1–32，每行等宽）", LabInput.Text(values, "pixels", Heart, 1200)),
        Select("packing", "打包方式", LabInput.Text(values, "packing", "page-lsb"), ("row-msb", "逐行：左像素为 bit7"), ("page-lsb", "纵向页：上像素为 bit0（每页 8 行）")),
        Check("flip", "水平翻转", LabInput.Boolean(values, "flip")),
        Check("invert", "反色（只反转有效像素，补齐位仍为 0）", LabInput.Boolean(values, "invert"))
    ];
    public override LabResult Calculate(JsonElement values)
    {
        var pattern = LabInput.Text(values, "pattern", "heart");
        var text = pattern switch
        {
            "heart" => Heart,
            "smile" => Smile,
            "alien" => Alien,
            "custom" => LabInput.Text(values, "pixels", Heart, 1200),
            _ => throw new ArgumentException("未知图案。")
        };
        var rows = text.Trim().Split('/').Select(row => row.Trim()).ToArray();
        var width = rows[0].Length;
        var height = rows.Length;
        if (width is < 1 or > 32 || height is < 1 or > 32 || rows.Any(row => row.Length != width || row.Any(c => c is not ('0' or '1'))))
        {
            throw new ArgumentException("图案宽高须为 1–32；只接受 0/1，每行等宽，用 / 分隔。");
        }
        var flip = LabInput.Boolean(values, "flip");
        var invert = LabInput.Boolean(values, "invert");
        var pixels = rows.Select(row => Enumerable.Range(0, width).Select(x => (row[flip ? width - x - 1 : x] == '1') ^ invert).ToArray()).ToArray();
        var packing = LabInput.Text(values, "packing", "page-lsb");
        if (packing is not ("row-msb" or "page-lsb"))
        {
            throw new ArgumentException("未知点阵打包方式。");
        }
        var stride = packing == "row-msb" ? (width + 7) / 8 : width;
        var data = new byte[packing == "row-msb" ? stride * height : width * ((height + 7) / 8)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!pixels[y][x])
                {
                    continue;
                }
                if (packing == "row-msb")
                {
                    data[y * stride + x / 8] |= (byte)(1 << (7 - x % 8));
                }
                else
                {
                    data[(y / 8) * width + x] |= (byte)(1 << (y % 8));
                }
            }
        }
        var preview = string.Join("\n", pixels.Select(row => string.Concat(row.Select(pixel => pixel ? "⬛" : "⬜"))));
        var code = $"#include <stdint.h>\n/* {width}x{height}; {packing}; padding bits = 0 */\nstatic const uint8_t bitmap[{data.Length}] = {{\n" +
            string.Join("\n", data.Chunk(16).Select(chunk => "    " + string.Join(", ", chunk.Select(value => $"0x{value:X2}")) + ",")) + "\n};";
        return new(new
        {
            width,
            height,
            packing,
            stride,
            bytes = data.Select(value => (int)value).ToArray(),
            preview,
            litPixels = pixels.Sum(row => row.Count(pixel => pixel))
        }, code,
        [
            Text("dimensions", "图案", $"{width} × {height} 像素，{data.Length} 字节；1=亮，0=灭。"),
            Text("preview", "像素预览\n", preview),
            Text("layout", "字节布局", packing == "row-msb" ? "从上到下逐行，每行从左到右；左侧像素放在 bit7，不足 8 列在右侧补 0。" : "页优先、页内从左到右；每页 8 行，顶部像素放在 bit0，末页不足 8 行在下方补 0。"),
            Text("driver", "接入提示", "需让你的显示驱动使用相同的扫描方向和字节布局；这里只生成图案数据。")
        ]);
    }
}

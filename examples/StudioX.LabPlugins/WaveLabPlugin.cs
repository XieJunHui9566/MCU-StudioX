namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using static LabPanel;

public sealed class WaveLabPlugin : LabPlugin
{
    public override string Title => "波形实验室";
    protected override string Introduction => "生成一个周期的正弦波、三角波、锯齿波或 PWM。曲线与 uint16_t 查表数组用于离线实验，不输出真实电压。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["shape"] = EnumSchema("sine", "triangle", "saw", "pwm"),
        ["samples"] = NumberSchema(8, 256, true),
        ["frequency"] = NumberSchema(0.01, 1000000),
        ["maximum"] = NumberSchema(1, 65535, true),
        ["duty"] = NumberSchema(0, 100)
    });
    protected override PluginPanelWidget[] Inputs(JsonElement values) =>
    [
        Select("shape", "波形", LabInput.Text(values, "shape", "sine"), ("sine", "正弦波"), ("triangle", "三角波"), ("saw", "锯齿波"), ("pwm", "PWM")),
        Number("samples", "每周期采样点数（8–256）", LabInput.Integer(values, "samples", 64, 8, 256)),
        Number("frequency", "目标频率 / Hz", LabInput.Number(values, "frequency", 100, 0.01, 1000000)),
        Number("maximum", "峰值代码（例如 12 位 DAC 填 4095）", LabInput.Integer(values, "maximum", 4095, 1, 65535)),
        Number("duty", "PWM 占空比 / %（其他波形忽略）", LabInput.Number(values, "duty", 25, 0, 100))
    ];
    public override LabResult Calculate(JsonElement values)
    {
        var shape = LabInput.Text(values, "shape", "sine");
        if (shape is not ("sine" or "triangle" or "saw" or "pwm"))
        {
            throw new ArgumentException("未知波形。");
        }
        var count = LabInput.Integer(values, "samples", 64, 8, 256);
        var frequency = LabInput.Number(values, "frequency", 100, 0.01, 1000000);
        var maximum = LabInput.Integer(values, "maximum", 4095, 1, 65535);
        var duty = LabInput.Number(values, "duty", 25, 0, 100);
        var highCount = (int)Math.Round(count * duty / 100, MidpointRounding.AwayFromZero);
        var samples = Enumerable.Range(0, count).Select(index =>
        {
            var phase = (double)index / count;
            var normalized = shape switch
            {
                "sine" => (1 - Math.Cos(2 * Math.PI * phase)) / 2,
                "triangle" => 1 - Math.Abs(2 * phase - 1),
                "saw" => phase,
                _ => index < highCount ? 1d : 0d
            };
            return (ushort)Math.Clamp(Math.Round(normalized * maximum, MidpointRounding.AwayFromZero), 0, maximum);
        }).ToArray();
        var rate = frequency * count;
        var quantizedDuty = highCount * 100d / count;
        var code = $"#include <stdint.h>\n/* {shape}; target {frequency.ToString("G9", CultureInfo.InvariantCulture)} Hz; update {rate.ToString("G9", CultureInfo.InvariantCulture)} samples/s */\nstatic const uint16_t waveform[{count}] = {{\n" +
            string.Join("\n", samples.Chunk(16).Select(chunk => "    " + string.Join(", ", chunk) + ",")) + "\n};\n";
        return new(new
        {
            shape,
            samples,
            frequency,
            sampleRate = rate,
            maximum,
            requestedDuty = duty,
            quantizedDuty
        }, code,
        [
            Text("summary", "采样要求", $"一个周期 {count} 点；更新率 {rate.ToString("G9", CultureInfo.InvariantCulture)} samples/s；区间 [0, 1/f)，不重复周期末点。"),
            Text("pwm", "PWM 量化", shape == "pwm" ? $"高电平 {highCount}/{count} 点，实际占空比 {quantizedDuty.ToString("0.####", CultureInfo.InvariantCulture)}%。" : "正弦波从谷值开始；三角波 0→峰值→0；锯齿波在下一周期跳回 0。"),
            new("wave", "plot", "一个周期：X=时间 / ms，Y=输出代码（PWM 相邻采样连线不代表模拟斜坡）", Json(samples.Select((value, index) => new { x = index * 1000 / rate, y = value }))),
            Text("limits", "使用提示", "实际定时器和 DAC/外设能否达到此更新率，需按目标器件配置；输出数组没有初始化硬件。")
        ]);
    }
}

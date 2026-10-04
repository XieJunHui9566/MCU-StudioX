namespace StudioX.MakerPlugins;

using System.Globalization;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;
using static StudioX.LabPlugins.LabPanel;

public sealed class DebounceStudioPlugin : LabPlugin
{
    public override string Title => "按键消抖实验室";
    protected override string Introduction => "输入开关电平变化，比较原始抖动和消抖事件，生成固定周期调用的 C 状态机。这里模拟电平，不读取真实 GPIO。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["trace"] = StringSchema(1024),
        ["sampleMs"] = NumberSchema(1, 10, true),
        ["debounceMs"] = NumberSchema(1, 100, true),
        ["durationMs"] = NumberSchema(50, 1000, true),
        ["algorithm"] = EnumSchema("stable", "integrator"),
        ["activeLow"] = new
        {
            type = "boolean"
        }
    });
    protected override PluginPanelWidget[] Inputs(JsonElement v) =>
    [
        Input("trace","变化序列 ms:电平；必须从 0ms 开始，时间递增",LabInput.Text(v,"trace","0:1 10:0 12:1 15:0 17:1 20:0 100:1 102:0 106:1")),
        Select("algorithm","消抖算法",LabInput.Text(v,"algorithm","stable"),("stable","电平持续稳定计时"),("integrator","饱和增减计数器")),
        Number("sampleMs","采样周期 / ms",LabInput.Integer(v,"sampleMs",1,1,10)),
        Number("debounceMs","消抖时间 / ms（向上量化至采样周期）",LabInput.Integer(v,"debounceMs",20,1,100)),
        Number("durationMs","仿真时长 / ms",LabInput.Integer(v,"durationMs",160,50,1000)),
        Check("activeLow","低电平代表按下",LabInput.Boolean(v,"activeLow",true))
    ];

    public override LabResult Calculate(JsonElement v)
    {
        var text = LabInput.Text(v, "trace", "0:1 10:0 12:1 15:0 17:1 20:0 100:1 102:0 106:1", 1024);
        var algorithm = LabInput.Text(v, "algorithm", "stable");
        if (algorithm is not ("stable" or "integrator"))
        {
            throw new ArgumentException("未知消抖算法。");
        }
        var sample = LabInput.Integer(v, "sampleMs", 1, 1, 10);
        var debounce = LabInput.Integer(v, "debounceMs", 20, 1, 100);
        var duration = LabInput.Integer(v, "durationMs", 160, 50, 1000);
        var activeLow = LabInput.Boolean(v, "activeLow", true);
        var changes = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            var parts = token.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var at) || parts[1] is not ("0" or "1"))
            {
                throw new ArgumentException("变化格式为 ms:0 或 ms:1，例：0:1 10:0 12:1 20:0。");
            }
            return (At: at, Level: parts[1] == "1");
        }).ToArray();
        if (changes.Length is < 1 or > 32 || changes[0].At != 0 || changes[^1].At > duration || changes.Zip(changes.Skip(1)).Any(pair => pair.First.At >= pair.Second.At))
        {
            throw new ArgumentException("需要 1–32 个严格递增的变化；首项为 0ms，末项不超过仿真时长。");
        }
        var ticks = (debounce + sample - 1) / sample;
        var state = changes[0].Level != activeLow;
        var candidate = state;
        var counter = state ? ticks : 0;
        var stableTicks = 0;
        var cursor = 0;
        var events = new List<ButtonEvent>();
        var points = new List<object>();
        var accepted = new List<object>();
        for (var at = 0; at <= duration; at += sample)
        {
            while (cursor + 1 < changes.Length && changes[cursor + 1].At <= at)
            {
                cursor++;
            }
            var raw = changes[cursor].Level != activeLow;
            var before = state;
            if (at > 0)
            {
                if (algorithm == "stable")
                {
                    if (raw != candidate)
                    {
                        candidate = raw;
                        stableTicks = 0;
                    }
                    else if (stableTicks < ticks)
                    {
                        stableTicks++;
                    }
                    if (stableTicks >= ticks)
                    {
                        state = candidate;
                    }
                }
                else
                {
                    counter = Math.Clamp(counter + (raw ? 1 : -1), 0, ticks);
                    if (counter == ticks)
                    {
                        state = true;
                    }
                    else if (counter == 0)
                    {
                        state = false;
                    }
                }
            }
            if (state != before)
            {
                events.Add(new(at, state));
            }
        }
        // 阶梯曲线保留全部变化；没有按点抽样，短促抖动不会因绘图降采样消失。
        var lastRaw = changes[0].Level ? 1 : 0;
        points.Add(new
        {
            x = 0,
            y = lastRaw
        });
        foreach (var change in changes.Skip(1))
        {
            points.Add(new
            {
                x = change.At,
                y = lastRaw
            });
            lastRaw = change.Level ? 1 : 0;
            points.Add(new
            {
                x = change.At,
                y = lastRaw
            });
        }
        points.Add(new
        {
            x = duration,
            y = lastRaw
        });
        var lastAccepted = changes[0].Level != activeLow ? 1 : 0;
        accepted.Add(new
        {
            x = 0,
            y = lastAccepted
        });
        foreach (var item in events)
        {
            accepted.Add(new
            {
                x = item.TimeMs,
                y = lastAccepted
            });
            lastAccepted = item.Pressed ? 1 : 0;
            accepted.Add(new
            {
                x = item.TimeMs,
                y = lastAccepted
            });
        }
        accepted.Add(new
        {
            x = duration,
            y = lastAccepted
        });
        var code = $$"""
            #include <stdint.h>
            /* Call db_init once with the current physical GPIO level.
               Then call db_step every {{sample}} ms. ticks={{ticks}}.
               Return: +1 pressed, -1 released, 0 unchanged. No delays. */
            typedef struct { uint8_t state, candidate; uint16_t stable, count; } Debounce;
            static uint8_t db_pressed(uint8_t high) { return {{(activeLow ? "!high" : "!!high")}}; }
            static void db_init(Debounce *d, uint8_t high)
            {
                d->state = db_pressed(high); d->candidate = d->state;
                d->stable = 0; d->count = d->state ? {{ticks}} : 0;
            }
            static int db_step(Debounce *d, uint8_t high)
            {
                const uint16_t ticks = {{ticks}};
                uint8_t raw = db_pressed(high), old = d->state;
            {{(algorithm == "stable" ? "    if (raw != d->candidate) { d->candidate = raw; d->stable = 0; }\n    else if (d->stable < ticks) ++d->stable;\n    if (d->stable >= ticks) d->state = d->candidate;" : "    if (raw) { if (d->count < ticks) ++d->count; }\n    else if (d->count) --d->count;\n    if (d->count == ticks) d->state = 1;\n    else if (d->count == 0) d->state = 0;")}}
                return d->state == old ? 0 : (d->state ? 1 : -1);
            }
            """;
        return new(new
        {
            algorithm,
            sampleMs = sample,
            thresholdTicks = ticks,
            quantizedMs = ticks * sample,
            events
        }, code,
        [
            Text("summary","结果",$"{events.Count(e=>e.Pressed)} 次按下，{events.Count(e=>!e.Pressed)} 次释放；阈值 {ticks} 个周期 / {ticks*sample}ms。"),
            new("raw","plot","原始物理电平：X=ms，Y=0/1",Json(points)),
            new("debounced","plot","消抖后：X=ms，Y=1 按下 / 0 释放",Json(accepted)),
            Table("events","确认事件",["时间 ms","动作"],events.Select(e=>new[]{e.TimeMs.ToString(CultureInfo.InvariantCulture),e.Pressed?"按下":"释放"})),
            Text("timing","计时口径","稳定计时从发现变化开始等待完整阈值；增减计数器在变化当次采样就计数。初始状态直接接受，不产生事件；采样间短脉冲可能漏检。")
        ]);
    }

    public sealed record ButtonEvent(int TimeMs, bool Pressed);
}

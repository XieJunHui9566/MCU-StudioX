namespace StudioX.MakerPlugins;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;
using static StudioX.LabPlugins.LabPanel;

public sealed partial class MelodyStudioPlugin : LabPlugin
{
    public override string Title => "蜂鸣器旋律工坊";
    protected override string Introduction => "将音符变成频率与节拍表，查看音高时间线。适合无源蜂鸣器的 PWM 播放；面板仅生成曲谱，不播放电脑声音或连接硬件。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["preset"] = EnumSchema("startup", "scale", "custom"),
        ["score"] = StringSchema(1024),
        ["bpm"] = NumberSchema(30, 300, true),
        ["gate"] = NumberSchema(10, 100)
    });
    protected override PluginPanelWidget[] Inputs(JsonElement v) =>
    [
        Select("preset", "曲谱", LabInput.Text(v, "preset", "startup"), ("startup", "开机提示音"), ("scale", "上行音阶"), ("custom", "自定义")),
        Input("score", "自定义：音名+八度:拍数，空格分隔；R 为休止", LabInput.Text(v, "score", "C5:0.5 E5:0.5 G5:0.5 C6:1 R:0.5 C6:0.5")),
        Number("bpm", "速度 BPM（每分钟四分音符数）", LabInput.Integer(v, "bpm", 120, 30, 300)),
        Number("gate", "每个音符发声时长 / %（其余为间隔）", LabInput.Number(v, "gate", 85, 10, 100))
    ];

    public override LabResult Calculate(JsonElement v)
    {
        var preset = LabInput.Text(v, "preset", "startup");
        var score = preset switch
        {
            "startup" => "C5:0.5 E5:0.5 G5:0.5 C6:1 R:0.5 C6:0.5",
            "scale" => "C4:1 D4:1 E4:1 F4:1 G4:1 A4:1 B4:1 C5:2",
            "custom" => LabInput.Text(v, "score", "C5:1 R:0.5 G5:1", 1024),
            _ => throw new ArgumentException("未知曲谱选项。")
        };
        var bpm = LabInput.Integer(v, "bpm", 120, 30, 300);
        var gate = LabInput.Number(v, "gate", 85, 10, 100);
        var tokens = score.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 48)
        {
            throw new ArgumentException("曲谱需要 1–48 个音符或休止符。");
        }
        var notes = new List<Note>();
        var points = new List<object>();
        double elapsed = 0;
        var previousEnd = 0;
        foreach (var token in tokens)
        {
            var match = NotePattern().Match(token);
            if (!match.Success || !double.TryParse(match.Groups["beats"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var beats) || beats is < 0.125 or > 8)
            {
                throw new ArgumentException($"无效音符“{token}”。例：C4:1、F#4:0.5、Bb3:2、R:0.25；拍数 0.125–8。");
            }
            var name = match.Groups["name"].Value;
            var hz = 0;
            if (name != "R")
            {
                var semitone = name[0] switch
                {
                    'C' => 0,
                    'D' => 2,
                    'E' => 4,
                    'F' => 5,
                    'G' => 7,
                    'A' => 9,
                    _ => 11
                };
                semitone += match.Groups["acc"].Value switch
                {
                    "#" => 1,
                    "b" => -1,
                    _ => 0
                };
                var midi = (int.Parse(match.Groups["oct"].Value, CultureInfo.InvariantCulture) + 1) * 12 + semitone;
                hz = (int)Math.Round(440 * Math.Pow(2, (midi - 69) / 12d), MidpointRounding.AwayFromZero);
            }
            elapsed += beats * 60000 / bpm;
            // 对累计边界取整，避免每个音符单独取整造成长曲谱节拍漂移。
            var end = (int)Math.Round(elapsed, MidpointRounding.AwayFromZero);
            var duration = end - previousEnd;
            var on = hz == 0 ? 0 : (int)Math.Round(duration * gate / 100, MidpointRounding.AwayFromZero);
            notes.Add(new(token, hz, duration, on, previousEnd));
            points.Add(new
            {
                x = previousEnd,
                y = hz
            });
            points.Add(new
            {
                x = previousEnd + on,
                y = hz
            });
            points.Add(new
            {
                x = previousEnd + on,
                y = 0
            });
            points.Add(new
            {
                x = end,
                y = 0
            });
            previousEnd = end;
        }
        var code = "#include <stdint.h>\n/* A4=440 Hz; 12-tone equal temperament. hz=0: silence. */\n" +
            "typedef struct { uint16_t hz, duration_ms, on_ms; } MelodyNote;\n" + $"static const MelodyNote melody[{notes.Count}] = {{\n" +
            string.Join("\n", notes.Select(n => $"    {{{n.Hz}, {n.DurationMs}, {n.OnMs}}}, /* {n.Token} */")) +
            "\n};\n/* For each note: PWM(hz) for on_ms, mute for duration_ms-on_ms.\n   Use a timer/state machine; this table does not initialize a peripheral. */\n";
        return new(new
        {
            bpm,
            notes,
            totalMs = previousEnd
        }, code,
        [
            Text("summary", "曲谱", $"{notes.Count} 个音符/休止符，共 {previousEnd} ms；十二平均律，A4=440 Hz，输出频率四舍五入到整数 Hz。"),
            new("pitch", "plot", "音高时间线：X=ms，Y=Hz；0 为静音", Json(points)),
            Table("notes", "播放顺序", ["音符", "频率 Hz", "时长 ms", "发声 ms"], notes.Select(n => new[] { n.Token, n.Hz.ToString(CultureInfo.InvariantCulture), n.DurationMs.ToString(CultureInfo.InvariantCulture), n.OnMs.ToString(CultureInfo.InvariantCulture) })),
            Text("usage", "接入工程", "使用定时器改变 PWM 频率；发声 on_ms 后关闭输出，补足本音符 duration_ms。先按实际时钟计算定时器参数。")
        ]);
    }

    [GeneratedRegex(@"^(?:(?<name>[A-G])(?<acc>[#b]?)(?<oct>[0-8])|(?<name>R)):(?<beats>[0-9]+(?:\.[0-9]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex NotePattern();

    public sealed record Note(string Token, int Hz, int DurationMs, int OnMs, int StartMs);
}

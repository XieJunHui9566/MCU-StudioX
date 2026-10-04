namespace StudioX.Engine.Hdl;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>有界读取 VCD 标量、总线、别名及 X/Z；超限或格式错误明确报错，不返回伪完整波形。</summary>
public static class VcdReader
{
    public static HdlWaveform Read(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
        {
            throw Error("VCD 超过 32 MiB，请缩短时间或减少记录信号。");
        }
        var text = File.ReadAllText(path);
        var tokens = new TokenReader(text);
        var scopes = new List<string>();
        var signals = new List<(string Name, int Width, string Id)>();
        var changes = new Dictionary<string, List<HdlWaveChange>>(StringComparer.Ordinal);
        decimal scale = 1;
        long tick = 0;
        var count = 0;
        var header = false;
        while (tokens.TryRead(out var word))
        {
            if (word == "$enddefinitions")
            {
                header = true;
                continue;
            }
            if (word is "$dumpvars" or "$dumpon" or "$dumpoff" or "$dumpall" or "$end")
            {
                continue;
            }
            if (word.StartsWith('$'))
            {
                var fields = new List<string>();
                while (true)
                {
                    if (!tokens.TryRead(out var field))
                    {
                        throw Error("VCD 指令缺少 $end。");
                    }
                    if (field == "$end")
                    {
                        break;
                    }
                    fields.Add(field);
                }
                var body = fields.ToArray();
                switch (word)
                {
                    case "$scope" when body.Length >= 2:
                        scopes.Add(body[1]);
                        break;
                    case "$upscope" when scopes.Count > 0:
                        scopes.RemoveAt(scopes.Count - 1);
                        break;
                    case "$timescale":
                        var match = Regex.Match(string.Concat(body), @"\A(1|10|100)(s|ms|us|ns|ps|fs)\z");
                        if (!match.Success)
                        {
                            throw Error("不支持的 VCD 时间单位。");
                        }
                        scale = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (match.Groups[2].Value switch
                        {
                            "s" => 1000000000m,
                            "ms" => 1000000m,
                            "us" => 1000m,
                            "ns" => 1m,
                            "ps" => .001m,
                            _ => .000001m
                        });
                        break;
                    case "$var" when body.Length >= 4:
                        if (!int.TryParse(body[1], out var width) || width is < 1 or > 4096 || signals.Count >= 2048)
                        {
                            throw Error("VCD 信号数量或位宽超限。");
                        }
                        if (body[0] is "real" or "realtime" or "string")
                        {
                            break;
                        }
                        signals.Add((string.Join('.', scopes.Append(string.Concat(body[3..]))), width, body[2]));
                        changes.TryAdd(body[2], []);
                        break;
                }
                continue;
            }
            if (!header)
            {
                throw Error("VCD 缺少完整信号定义。");
            }
            if (word.StartsWith('#'))
            {
                if (!long.TryParse(word.AsSpan(1), out var next) || next < tick)
                {
                    throw Error("VCD 时间戳无效或倒退。");
                }
                tick = next;
                continue;
            }
            string id;
            string value;
            if (word[0] is 'b' or 'B' or 'r' or 'R' or 's' or 'S')
            {
                if (!tokens.TryRead(out id))
                {
                    throw Error("VCD 数值缺少信号编号。");
                }
                if (word[0] is not ('b' or 'B'))
                {
                    continue;
                }
                value = word[1..].ToLowerInvariant();
            }
            else
            {
                id = word[1..];
                value = word[..1].ToLowerInvariant();
            }
            if (!changes.TryGetValue(id, out var events))
            {
                throw Error("VCD 包含未定义的数字信号编号。");
            }
            if (value.Length == 0 || value.Any(character => character is not ('0' or '1' or 'x' or 'z')))
            {
                throw Error("VCD 数字信号数值无效。");
            }
            if (++count > 1000000)
            {
                throw Error("VCD 超过一百万次变化，请缩短仿真时间。");
            }
            if (events.Count > 0 && events[^1].Tick == tick)
            {
                events[^1] = new(tick, value);
            }
            else if (events.Count == 0 || events[^1].Value != value)
            {
                events.Add(new(tick, value));
            }
        }
        if (!header || signals.Count == 0)
        {
            throw Error("VCD 中没有可显示的数字信号。");
        }
        // VCD 的端口别名共用事件数组，避免多个层级把同一百万事件复制数十份。
        var arrays = changes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        return new(scale, tick, signals.Select(signal => new HdlWaveSignal(signal.Name, signal.Width, arrays[signal.Id])).ToArray());
    }

    private static StudioXException Error(string message) => new("HDL_VCD", message);

    // 按需分词，避免 32 MiB 波形先膨胀成数百万个正则 Match 对象。
    private sealed class TokenReader(string text)
    {
        private int position;
        internal bool TryRead(out string token)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            var start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            token = text[start..position];
            return position > start;
        }
    }
}

namespace StudioX.Engine.Debugging;

using System.Text;

/// <summary>GDB/MI 的 tuple/list 保留重复字段（例如 stack=[frame=...,frame=...]）。</summary>
public sealed record MiValue(string? Text, IReadOnlyList<KeyValuePair<string, MiValue>> Children)
{
    public MiValue? Get(string name) => Children.FirstOrDefault(x => x.Key == name).Value;
    public string String(string name, string fallback = "") => Get(name)?.Text ?? fallback;
    public IEnumerable<MiValue> Values => Children.Select(x => x.Value);
    public static MiValue Scalar(string text) => new(text, []);
}

public sealed record MiRecord(int? Token, char Kind, string Class, MiValue Data)
{
    public static MiRecord Parse(string text) => new Reader(text).Read();
    public static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var c in text) result.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        return result.Append('"').ToString();
    }
    private sealed class Reader(string source)
    {
        private int index;
        public MiRecord Read()
        {
            var begin = index;
            while (index < source.Length && char.IsAsciiDigit(source[index])) index++;
            int? token = index > begin ? int.Parse(source[begin..index], System.Globalization.CultureInfo.InvariantCulture) : null;
            if (index >= source.Length) throw new FormatException("Incomplete GDB/MI record.");
            var kind = source[index++];
            if (kind is '~' or '@' or '&') return new(token, kind, "stream", Value(0));
            if (kind is not ('^' or '*' or '+' or '=')) throw new FormatException("Unknown GDB/MI record.");
            var name = Word();
            var values = new List<KeyValuePair<string, MiValue>>();
            while (index < source.Length && source[index] == ',') { index++; values.Add(Field(0)); }
            if (index != source.Length) throw new FormatException("Trailing GDB/MI data.");
            return new(token, kind, name, new(null, values));
        }
        private string Word()
        {
            var start = index;
            while (index < source.Length && source[index] is not (',' or '=' or '}' or ']')) index++;
            if (start == index) throw new FormatException("Missing GDB/MI field.");
            return source[start..index];
        }
        private KeyValuePair<string, MiValue> Field(int depth)
        {
            if (index < source.Length && source[index] is '"' or '{' or '[') return new("", Value(depth));
            var key = Word();
            if (index >= source.Length || source[index++] != '=') throw new FormatException("Missing GDB/MI assignment.");
            return new(key, Value(depth));
        }
        private MiValue Value(int depth)
        {
            if (depth > 32 || index >= source.Length) throw new FormatException("Invalid GDB/MI nesting.");
            if (source[index] == '"')
            {
                index++; var value = new StringBuilder();
                while (index < source.Length)
                {
                    var c = source[index++];
                    if (c == '"') return MiValue.Scalar(value.ToString());
                    if (c != '\\') { value.Append(c); continue; }
                    if (index >= source.Length) break;
                    c = source[index++];
                    if (c is >= '0' and <= '7')
                    {
                        var number = c - '0';
                        for (var n = 1; n < 3 && index < source.Length && source[index] is >= '0' and <= '7'; n++) number = number * 8 + source[index++] - '0';
                        value.Append((char)number);
                    }
                    else value.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', 'a' => '\a', _ => c });
                }
                throw new FormatException("Unterminated GDB/MI string.");
            }
            var open = source[index++];
            if (open is not ('{' or '[')) throw new FormatException("Expected GDB/MI value.");
            var close = open == '{' ? '}' : ']';
            var children = new List<KeyValuePair<string, MiValue>>();
            while (index < source.Length && source[index] != close)
            {
                children.Add(Field(depth + 1));
                if (index < source.Length && source[index] == ',') index++;
                else break;
            }
            if (index >= source.Length || source[index++] != close) throw new FormatException("Unterminated GDB/MI container.");
            return new(null, children);
        }
    }
}

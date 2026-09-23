namespace StudioX.Application.Serial;

using System.Text;

public sealed record TerminalStyle(int? Foreground = null, int? Background = null, bool Bold = false, bool Italic = false, bool Underline = false, bool Inverse = false);
public sealed record TerminalSpan(int Start, int Length, TerminalStyle Style);
public sealed record TerminalSnapshot(long Version, string Text, IReadOnlyList<TerminalSpan> Spans, long TrimmedLines);

/// <summary>有界串口文本终端。跨接收分片保留 ANSI 状态；仅解释显示序列，不执行 OSC/设备查询。</summary>
public sealed class AnsiTerminal
{
    private sealed record Cell(char Value, TerminalStyle Style);
    private sealed class Line(DateTimeOffset time) { public DateTimeOffset Time = time; public List<Cell> Cells = []; }
    private readonly List<Line> lines = [];
    private TerminalStyle style = new();
    private readonly StringBuilder sequence = new();
    private int state, row, column, savedRow, savedColumn;
    private long version, trimmed;
    private DateTimeOffset currentTime;
    public long Version => version;
    public void Clear() { lines.Clear(); style = new(); state = row = column = savedRow = savedColumn = 0; sequence.Clear(); trimmed = 0; version++; }
    public void ResetControl() { style = new(); state = 0; sequence.Clear(); version++; }
    public void Feed(string text, DateTimeOffset time, bool ansi)
    {
        currentTime = time;
        foreach (var ch in text)
        {
            if (!ansi) { if (ch == '\x1b') { foreach (var c in "\\x1B") Put(c); } else Normal(ch); continue; }
            switch (state)
            {
                case 0: if (ch == '\x1b') state = 1; else Normal(ch); break;
                case 1:
                    state = ch switch { '[' => 2, ']' or 'P' or '^' or '_' => 3, _ => 0 };
                    sequence.Clear();
                    if (ch == '7') { savedRow = row; savedColumn = column; }
                    if (ch == '8') { row = Math.Clamp(savedRow, 0, Math.Max(0, lines.Count - 1)); column = savedColumn; }
                    break;
                case 2:
                    if (ch is >= '@' and <= '~') { Apply(ch); state = 0; }
                    else if (sequence.Length < 128 && ch is >= ' ' and <= '?') sequence.Append(ch);
                    else { state = 0; sequence.Clear(); }
                    break;
                case 3: if (ch == '\a') state = 0; else if (ch == '\x1b') state = 4; break;
                case 4: state = ch == '\\' ? 0 : 3; break;
            }
        }
        version++;
    }
    public void Echo(string text, DateTimeOffset time)
    {
        var previous = style; var previousState = state;
        currentTime = time; style = new(0x61AFEF); state = 0;
        if (column != 0) NewLine();
        foreach (var ch in "[TX] " + text.Replace("\x1b", "\\x1B")) Normal(ch);
        if (column != 0) NewLine();
        style = previous; state = previousState; version++;
    }
    private void Normal(char ch)
    {
        switch (ch)
        {
            case '\r': column = 0; break;
            case '\n': NewLine(); break;
            case '\b': column = Math.Max(0, column - 1); break;
            case '\t': var next = Math.Min(512, (column / 8 + 1) * 8); while (column < next) Put(' '); break;
            default: if (ch >= ' ' && ch != '\x7f') Put(ch); break;
        }
    }
    private void EnsureLine() { while (lines.Count <= row) lines.Add(new(currentTime)); }
    private void NewLine()
    {
        row++; column = 0; EnsureLine();
        if (lines.Count > 2000) { lines.RemoveAt(0); row--; savedRow = Math.Max(0, savedRow - 1); trimmed++; }
    }
    private void Put(char ch)
    {
        if (column >= 512) NewLine();
        EnsureLine(); var line = lines[row];
        while (line.Cells.Count <= column) line.Cells.Add(new(' ', new()));
        line.Cells[column++] = new(ch, style);
    }
    private void Apply(char command)
    {
        var raw = sequence.ToString();
        if (raw.Any(c => !(char.IsAsciiDigit(c) || c is ';' or ':'))) return;
        // ISO 8613-6 冒号形式允许一个空的或 0 的颜色空间槽：38:2::r:g:b。
        if (command == 'm' && raw.Contains(':'))
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"(38|48):2:(?:0)?:([0-9]+):([0-9]+):([0-9]+)", "$1;2;$2;$3;$4");
        var values = raw.Replace(':', ';').Split(';').Select(s => int.TryParse(s, out var n) ? Math.Clamp(n, 0, 10000) : 0).ToArray();
        int P(int index, int fallback = 1) => index < values.Length && values[index] != 0 ? values[index] : fallback;
        switch (command)
        {
            case 'm': Sgr(values); break;
            case 'A': row = Math.Max(0, row - P(0)); break;
            case 'B': row = Math.Min(1999, row + P(0)); EnsureLine(); break;
            case 'C': column = Math.Min(511, column + P(0)); break;
            case 'D': column = Math.Max(0, column - P(0)); break;
            case 'G': column = Math.Clamp(P(0) - 1, 0, 511); break;
            case 'H': case 'f': row = Math.Clamp(P(0) - 1, 0, 1999); column = Math.Clamp(P(1) - 1, 0, 511); EnsureLine(); break;
            case 's': savedRow = row; savedColumn = column; break;
            case 'u': row = Math.Clamp(savedRow, 0, Math.Max(0, lines.Count - 1)); column = savedColumn; break;
            case 'K':
                EnsureLine(); var cells = lines[row].Cells;
                if (values[0] == 2) cells.Clear();
                else if (values[0] == 0 && column < cells.Count) cells.RemoveRange(column, cells.Count - column);
                else if (values[0] == 1) for (var i = 0; i <= column && i < cells.Count; i++) cells[i] = new(' ', style);
                break;
            case 'J':
                if (values[0] is 2 or 3) { lines.Clear(); row = column = 0; }
                else if (values[0] == 0) { EnsureLine(); if (column < lines[row].Cells.Count) lines[row].Cells.RemoveRange(column, lines[row].Cells.Count - column); if (row + 1 < lines.Count) lines.RemoveRange(row + 1, lines.Count - row - 1); }
                break;
        }
    }
    private static readonly int[] Palette = [0x1E1E1E,0xCD3131,0x0DBC79,0xE5C07B,0x2472C8,0xBC3FBC,0x11A8CD,0xD4D4D4,0x808080,0xF14C4C,0x23D18B,0xF5F543,0x3B8EEA,0xD670D6,0x29B8DB,0xFFFFFF];
    private static int Indexed(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16) return Palette[index];
        if (index >= 232) { var c = 8 + (index - 232) * 10; return c * 0x10101; }
        index -= 16; int C(int c) => c == 0 ? 0 : 55 + c * 40;
        return (C(index / 36) << 16) | (C(index / 6 % 6) << 8) | C(index % 6);
    }
    private void Sgr(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var code = values[i];
            style = code switch
            {
                0 => new(), 1 => style with { Bold = true }, 3 => style with { Italic = true }, 4 => style with { Underline = true }, 7 => style with { Inverse = true },
                22 => style with { Bold = false }, 23 => style with { Italic = false }, 24 => style with { Underline = false }, 27 => style with { Inverse = false },
                39 => style with { Foreground = null }, 49 => style with { Background = null },
                >= 30 and <= 37 => style with { Foreground = Palette[code - 30] }, >= 90 and <= 97 => style with { Foreground = Palette[code - 90 + 8] },
                >= 40 and <= 47 => style with { Background = Palette[code - 40] }, >= 100 and <= 107 => style with { Background = Palette[code - 100 + 8] }, _ => style
            };
            if (code is 38 or 48 && i + 1 < values.Length)
            {
                int? color = null;
                if (values[i + 1] == 5 && i + 2 < values.Length) { color = Indexed(values[i + 2]); i += 2; }
                else if (values[i + 1] == 2 && i + 4 < values.Length) { color = (Math.Clamp(values[i + 2], 0, 255) << 16) | (Math.Clamp(values[i + 3], 0, 255) << 8) | Math.Clamp(values[i + 4], 0, 255); i += 4; }
                if (color is not null) style = code == 38 ? style with { Foreground = color } : style with { Background = color };
            }
        }
    }
    public TerminalSnapshot Snapshot(bool timestamps)
    {
        var text = new StringBuilder(); var spans = new List<TerminalSpan>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (timestamps && line.Cells.Count > 0) text.Append('[').Append(line.Time.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ");
            var runStart = text.Length; TerminalStyle? runStyle = null;
            foreach (var cell in line.Cells)
            {
                if (runStyle != cell.Style)
                {
                    if (runStyle is not null) spans.Add(new(runStart, text.Length - runStart, runStyle));
                    runStart = text.Length; runStyle = cell.Style;
                }
                text.Append(cell.Value);
            }
            if (runStyle is not null) spans.Add(new(runStart, text.Length - runStart, runStyle));
            if (index < lines.Count - 1) text.Append('\n');
        }
        return new(version, text.ToString(), spans, trimmed);
    }
}

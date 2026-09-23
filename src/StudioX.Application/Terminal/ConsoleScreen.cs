namespace StudioX.Application.Terminal;

using System.Text;
using StudioX.Application.Serial;

public sealed record ConsoleSnapshot(TerminalSnapshot Display, int CursorOffset, bool CursorVisible);

/// <summary>ConPTY 的有限屏幕与滚动历史；只解释显示和光标查询，不执行 OSC 链接、剪贴板或命令。</summary>
public sealed class ConsoleScreen
{
    private sealed record Cell(string Text, TerminalStyle Style);
    private readonly Queue<Cell[]> history = new();
    private Cell[][] screen = [];
    private Cell[][]? mainScreen;
    private int row, col, savedRow, savedCol, top, bottom, state;
    private bool wrapPending, cursorVisible = true;
    private readonly StringBuilder sequence = new();
    private TerminalStyle style = new();
    private char? highSurrogate;
    private long version, trimmed;
    private ConsoleSnapshot? cached;
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public event Action<string>? Reply;
    public ConsoleScreen(int columns = 120, int rows = 20) => Resize(columns, rows);
    public void ClearHistory() { history.Clear(); trimmed = 0; version++; }
    private Cell[] Blank() => Enumerable.Range(0, Columns).Select(_ => new Cell(" ", new())).ToArray();
    public void Resize(int columns, int rows)
    {
        Columns = Math.Clamp(columns, 40, 240); Rows = Math.Clamp(rows, 6, 80);
        var next = Enumerable.Range(0, Rows).Select(_ => Blank()).ToArray();
        for (var r = 0; r < Math.Min(screen.Length, Rows); r++) Array.Copy(screen[r], next[r], Math.Min(screen[r].Length, Columns));
        screen = next; row = Math.Min(row, Rows - 1); col = Math.Min(col, Columns - 1); top = 0; bottom = Rows - 1; wrapPending = false; version++;
    }
    public void Feed(string text)
    {
        foreach (var ch in text)
        {
            if (state == 3) { if (ch == '\a') state = 0; else if (ch == '\x1b') state = 4; continue; }
            if (state == 4) { state = ch == '\\' ? 0 : 3; continue; }
            if (state == 1)
            {
                state = ch switch { '[' => 2, ']' or 'P' or '^' or '_' => 3, _ => 0 }; sequence.Clear();
                if (ch == '7') { savedRow = row; savedCol = col; }
                if (ch == '8') { row = Math.Min(savedRow, Rows - 1); col = Math.Min(savedCol, Columns - 1); }
                if (ch == 'M') { if (row == top) ScrollDown(1); else row = Math.Max(0, row - 1); }
                if (ch == 'D') LineFeed();
                continue;
            }
            if (state == 2)
            {
                if (ch is >= '@' and <= '~') { Apply(ch); state = 0; }
                else if (sequence.Length < 256) sequence.Append(ch); else { state = 0; sequence.Clear(); }
                continue;
            }
            switch (ch)
            {
                case '\x1b': state = 1; break;
                case '\r': col = 0; wrapPending = false; break;
                case '\n': LineFeed(); break;
                case '\b': col = Math.Max(0, col - 1); wrapPending = false; break;
                case '\t': col = Math.Min(Columns - 1, (col / 8 + 1) * 8); break;
                default:
                    if (char.IsHighSurrogate(ch)) highSurrogate = ch;
                    else if (ch >= ' ' && ch != '\x7f')
                    {
                        var value = highSurrogate is { } high && char.IsLowSurrogate(ch) ? new string([high, ch]) : ch.ToString();
                        highSurrogate = null; Put(value);
                    }
                    break;
            }
        }
        version++;
    }
    private void Put(string text)
    {
        var rune = Rune.GetRuneAt(text, 0).Value;
        var width = rune is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff01 and <= 0xff60 or >= 0x1f300 and <= 0x1faff ? 2 : 1;
        if (wrapPending || col + width > Columns) { col = 0; LineFeed(); }
        screen[row][col] = new(text, style);
        if (width == 2) screen[row][col + 1] = new("", style);
        col += width;
        if (col >= Columns) { col = Columns - 1; wrapPending = true; }
    }
    private void LineFeed() { wrapPending = false; if (row == bottom) ScrollUp(1); else row = Math.Min(Rows - 1, row + 1); }
    private void ScrollUp(int count)
    {
        for (var i = 0; i < Math.Min(count, bottom - top + 1); i++)
        {
            if (mainScreen is null && top == 0 && bottom == Rows - 1)
            { history.Enqueue(screen[0]); if (history.Count > 1000) { history.Dequeue(); trimmed++; } }
            for (var r = top; r < bottom; r++) screen[r] = screen[r + 1];
            screen[bottom] = Blank();
        }
    }
    private void ScrollDown(int count)
    {
        for (var i = 0; i < Math.Min(count, bottom - top + 1); i++)
        { for (var r = bottom; r > top; r--) screen[r] = screen[r - 1]; screen[top] = Blank(); }
    }
    private void Erase(int r, int from, int to) { for (var c = from; c <= to; c++) screen[r][c] = new(" ", style); }
    private void Apply(char command)
    {
        var raw = sequence.ToString(); var privateMode = raw.StartsWith('?');
        var values = raw.TrimStart('?', '>').Split(';').Select(s => int.TryParse(s, out var n) ? Math.Clamp(n, 0, 10000) : 0).ToArray();
        int P(int i, int fallback = 1) => i < values.Length && values[i] != 0 ? values[i] : fallback;
        if (privateMode)
        {
            if (command is 'h' or 'l') foreach (var v in values)
            {
                if (v == 25) cursorVisible = command == 'h';
                if (v is 1049 or 47 or 1047)
                {
                    if (command == 'h' && mainScreen is null) { mainScreen = screen; screen = Enumerable.Range(0, Rows).Select(_ => Blank()).ToArray(); savedRow = row; savedCol = col; row = col = 0; }
                    if (command == 'l' && mainScreen is not null) { screen = mainScreen; mainScreen = null; Resize(Columns, Rows); row = Math.Min(savedRow, Rows - 1); col = Math.Min(savedCol, Columns - 1); }
                }
            }
            return;
        }
        if (command != 'm') wrapPending = false;
        switch (command)
        {
            case 'A': row = Math.Max(0, row - P(0)); break;
            case 'B': row = Math.Min(Rows - 1, row + P(0)); break;
            case 'C': col = Math.Min(Columns - 1, col + P(0)); break;
            case 'D': col = Math.Max(0, col - P(0)); break;
            case 'E': row = Math.Min(Rows - 1, row + P(0)); col = 0; break;
            case 'F': row = Math.Max(0, row - P(0)); col = 0; break;
            case 'G': col = Math.Clamp(P(0) - 1, 0, Columns - 1); break;
            case 'd': row = Math.Clamp(P(0) - 1, 0, Rows - 1); break;
            case 'H': case 'f': row = Math.Clamp(P(0) - 1, 0, Rows - 1); col = Math.Clamp(P(1) - 1, 0, Columns - 1); break;
            case 's': savedRow = row; savedCol = col; break;
            case 'u': row = Math.Min(savedRow, Rows - 1); col = Math.Min(savedCol, Columns - 1); break;
            case 'K': Erase(row, values[0] == 0 ? col : 0, values[0] == 1 ? col : Columns - 1); break;
            case 'J':
                if (values[0] == 3) history.Clear();
                if (values[0] >= 2) { for (var r = 0; r < Rows; r++) Erase(r, 0, Columns - 1); }
                else if (values[0] == 0) { Erase(row, col, Columns - 1); for (var r = row + 1; r < Rows; r++) Erase(r, 0, Columns - 1); }
                else { Erase(row, 0, col); for (var r = 0; r < row; r++) Erase(r, 0, Columns - 1); }
                break;
            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
            case 'r': top = Math.Clamp(P(0) - 1, 0, Rows - 1); bottom = Math.Clamp(P(1, Rows) - 1, top, Rows - 1); row = col = 0; break;
            case 'L': case 'M': var oldTop = top; top = row; if (command == 'L') ScrollDown(P(0)); else ScrollUp(P(0)); top = oldTop; break;
            case 'X': Erase(row, col, Math.Min(Columns - 1, col + P(0) - 1)); break;
            case '@': case 'P':
                var n = Math.Min(P(0), Columns - col);
                if (command == '@') { Array.Copy(screen[row], col, screen[row], col + n, Columns - col - n); Erase(row, col, col + n - 1); }
                else { Array.Copy(screen[row], col + n, screen[row], col, Columns - col - n); Erase(row, Columns - n, Columns - 1); }
                break;
            case 'm': Sgr(values); break;
            case 'n': if (values[0] == 6) Reply?.Invoke($"\x1b[{row + 1};{col + 1}R"); else if (values[0] == 5) Reply?.Invoke("\x1b[0n"); break;
            case 'c': Reply?.Invoke("\x1b[?1;0c"); break;
            case 't': if (values[0] == 18) Reply?.Invoke($"\x1b[8;{Rows};{Columns}t"); break;
        }
    }
    private static readonly int[] Palette = [0x25262b,0xe06c75,0x98c379,0xe5c07b,0x61afef,0xc678dd,0x56b6c2,0xdfe1e5,0x777b85,0xff8585,0xb5e890,0xffdc8a,0x85bdff,0xe5a0ff,0x75dfdf,0xffffff];
    private static int Color(int index)
    {
        index = Math.Clamp(index, 0, 255); if (index < 16) return Palette[index];
        if (index >= 232) return (8 + (index - 232) * 10) * 0x10101;
        index -= 16; int C(int n) => n == 0 ? 0 : 55 + n * 40;
        return C(index / 36) << 16 | C(index / 6 % 6) << 8 | C(index % 6);
    }
    private void Sgr(int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            style = v switch
            {
                0 => new(), 1 => style with { Bold = true }, 3 => style with { Italic = true }, 4 => style with { Underline = true }, 7 => style with { Inverse = true },
                22 => style with { Bold = false }, 23 => style with { Italic = false }, 24 => style with { Underline = false }, 27 => style with { Inverse = false },
                39 => style with { Foreground = null }, 49 => style with { Background = null },
                >= 30 and <= 37 => style with { Foreground = Palette[v - 30] }, >= 90 and <= 97 => style with { Foreground = Palette[v - 90 + 8] },
                >= 40 and <= 47 => style with { Background = Palette[v - 40] }, >= 100 and <= 107 => style with { Background = Palette[v - 100 + 8] }, _ => style
            };
            if (v is 38 or 48 && i + 1 < values.Length)
            {
                int? rgb = null;
                if (values[i + 1] == 5 && i + 2 < values.Length) { rgb = Color(values[i + 2]); i += 2; }
                else if (values[i + 1] == 2 && i + 4 < values.Length) { rgb = Math.Clamp(values[i + 2], 0, 255) << 16 | Math.Clamp(values[i + 3], 0, 255) << 8 | Math.Clamp(values[i + 4], 0, 255); i += 4; }
                if (rgb is not null) style = v == 38 ? style with { Foreground = rgb } : style with { Background = rgb };
            }
        }
    }
    public ConsoleSnapshot Snapshot()
    {
        if (cached?.Display.Version == version) return cached;
        var text = new StringBuilder(); var spans = new List<TerminalSpan>(); var cursor = -1;
        var lines = (mainScreen is null ? history : []).Concat(screen).ToArray();
        var cursorRow = (mainScreen is null ? history.Count : 0) + row;
        for (var r = 0; r < lines.Length; r++)
        {
            var line = lines[r]; var length = line.Length;
            while (length > 0 && line[length - 1].Text == " " && !(r == cursorRow && length - 1 <= col)) length--;
            for (var c = 0; c < length; c++)
            {
                // ConPTY 会临时隐藏光标；跟随输出仍需要它的位置，不能滞留在旧滚动偏移。
                if (r == cursorRow && c == col) cursor = text.Length;
                var cell = line[c]; var start = text.Length; text.Append(cell.Text);
                if (spans.Count > 0 && spans[^1].Style == cell.Style && spans[^1].Start + spans[^1].Length == start)
                    spans[^1] = spans[^1] with { Length = spans[^1].Length + cell.Text.Length };
                else if (cell.Text.Length > 0) spans.Add(new(start, cell.Text.Length, cell.Style));
            }
            if (r != lines.Length - 1) text.Append('\n');
        }
        return cached = new(new(version, text.ToString(), spans, trimmed), cursor, cursorVisible);
    }
}

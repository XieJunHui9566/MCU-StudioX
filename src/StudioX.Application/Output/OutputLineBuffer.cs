namespace StudioX.Application.Output;

using System.Text;

/// <summary>合流输出块按行呈现，支持跨块 CRLF；原始分流工具日志仍由构建引擎保留。</summary>
public sealed class OutputLineBuffer
{
    private readonly StringBuilder pending = new();
    private bool afterCarriageReturn;
    public IReadOnlyList<string> Append(string chunk)
    {
        var lines = new List<string>();
        foreach (var character in chunk)
        {
            if (character is '\r' or '\n')
            {
                if (character != '\n' || !afterCarriageReturn)
                {
                    lines.Add(pending.ToString());
                    pending.Clear();
                }
                afterCarriageReturn = character == '\r';
            }
            else
            {
                afterCarriageReturn = false;
                pending.Append(character);
                // 超长无换行诊断分段显示，不丢字符，也不让 UI 缓冲无界增长。
                if (pending.Length >= 65536) {lines.Add(pending.ToString()); pending.Clear();}
            }
        }
        return lines;
    }
    public string? Flush()
    {
        if (pending.Length == 0) {return null;}
        var text = pending.ToString();
        pending.Clear();
        return text;
    }
}

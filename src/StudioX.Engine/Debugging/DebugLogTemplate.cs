namespace StudioX.Engine.Debugging;

using System.Text;

/// <summary>文本中的 {只读表达式} 由调试器求值；双花括号表示字面花括号。</summary>
public sealed record DebugLogPart(string Text, bool Expression);
public static class DebugLogTemplate
{
    public static IReadOnlyList<DebugLogPart> Parse(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 1000 || template.Any(char.IsControl)) throw new ArgumentException("日志请输入 1–1000 字符的单行文本。");
        var result = new List<DebugLogPart>(); var text = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c is '{' or '}' && i + 1 < template.Length && template[i + 1] == c) { text.Append(c); i++; continue; }
            if (c == '}') throw new ArgumentException("日志花括号未配对；字面花括号请写成 {{ 和 }}。");
            if (c != '{') { text.Append(c); continue; }
            if (text.Length > 0) { result.Add(new(text.ToString(), false)); text.Clear(); }
            var end = template.IndexOf('}', i + 1);
            if (end < 0) throw new ArgumentException("日志表达式缺少 }。");
            var expression = template[(i + 1)..end].Trim(); _ = DebugExpression.Parse(expression);
            result.Add(new(expression, true)); i = end;
        }
        if (text.Length > 0) result.Add(new(text.ToString(), false));
        if (result.Count(p => p.Expression) > 12) throw new ArgumentException("每条日志最多插入 12 个表达式。");
        return result;
    }
}

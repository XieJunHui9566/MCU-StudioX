namespace StudioX.LabPlugins;

/// <summary>有界整数表达式解析器；只识别数字和运算符，不执行脚本或调用系统计算器。</summary>
internal sealed class ProgrammerExpression(ProgrammerWord word, string expression)
{
    private int position;
    private int tokens;
    private int depth;
    private string token = "";

    internal ulong Evaluate()
    {
        if (expression.Length is < 1 or > 1024)
        {
            throw new ArgumentException("表达式须为 1–1024 个字符。");
        }
        Next();
        var value = Binary(1);
        if (token != "")
        {
            throw new ArgumentException("表达式中存在多余字符或缺少运算符：" + token);
        }
        return value;
    }

    private ulong Binary(int minimum)
    {
        var left = Primary();
        while (Priority(token) >= minimum)
        {
            var operation = token;
            var priority = Priority(operation);
            Next();
            left = word.Apply(operation, left, Binary(priority + 1));
        }
        return left;
    }

    private ulong Primary()
    {
        if (++depth > 32)
        {
            throw new ArgumentException("表达式括号或一元运算嵌套最多 32 层。");
        }
        try
        {
            var current = token;
            if (current is "+" or "-" or "~" or "not")
            {
                Next();
                // 负字面量先按有符号范围检查，避免把超范围负数静默取模。
                if (current == "-" && IsNumber(token))
                {
                    var value = word.Parse("-" + token);
                    Next();
                    return value;
                }
                return word.Unary(current, Primary());
            }
            if (current == "(")
            {
                Next();
                var value = Binary(1);
                if (token != ")")
                {
                    throw new ArgumentException("表达式缺少右括号 )。");
                }
                Next();
                return value;
            }
            if (!IsNumber(current))
            {
                throw new ArgumentException("这里需要整数或左括号：" + current);
            }
            Next();
            return word.Parse(current);
        }
        finally { depth--; }
    }

    private static int Priority(string value) => value switch
    {
        "|" or "or" or "nor" => 1,
        "^" or "xor" => 2,
        "&" or "and" or "nand" => 3,
        "<<" or ">>" or ">>>" or "shl" or "shr" or "rol" or "ror" => 4,
        "+" or "-" => 5,
        "*" or "/" or "%" => 6,
        _ => 0
    };

    private static bool IsNumber(string value) => value.Length > 0 && value is not
        ("and" or "or" or "xor" or "not" or "nand" or "nor" or "shl" or "shr" or "rol" or "ror") && char.IsAsciiHexDigit(value[0]);

    private void Next()
    {
        while (position < expression.Length && char.IsWhiteSpace(expression[position]))
        {
            position++;
        }
        if (position == expression.Length)
        {
            token = "";
            return;
        }
        if (++tokens > 256)
        {
            throw new ArgumentException("表达式最多包含 256 个数字与运算符。");
        }
        var start = position++;
        var ch = expression[start];
        if (char.IsAsciiLetterOrDigit(ch))
        {
            while (position < expression.Length && (char.IsAsciiLetterOrDigit(expression[position]) || expression[position] == '_'))
            {
                position++;
            }
        }
        else if (ch is '<' or '>')
        {
            if (position >= expression.Length || expression[position] != ch)
            {
                throw new ArgumentException("移位请使用 << / >> / >>>。");
            }
            position++;
            if (ch == '>' && position < expression.Length && expression[position] == '>')
            {
                position++;
            }
        }
        else if (ch is not ('(' or ')' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' or '~'))
        {
            throw new ArgumentException("表达式包含不支持的字符：" + ch);
        }
        token = expression[start..position].ToLowerInvariant();
    }
}

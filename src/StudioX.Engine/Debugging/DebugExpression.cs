namespace StudioX.Engine.Debugging;

using System.Globalization;

/// <summary>只读整数表达式子集。离线使用有符号 64 位计算，不解释任意 C，不执行赋值、调用或解引用。</summary>
public sealed class DebugExpression
{
    private abstract record Node
    {
        public abstract long Evaluate(Func<string, long> lookup);
        public abstract IEnumerable<string> Symbols { get; }
    }
    private sealed record Constant(long Value) : Node
    {
        public override long Evaluate(Func<string, long> lookup) => Value;
        public override IEnumerable<string> Symbols => [];
    }
    private sealed record Symbol(string Name) : Node
    {
        public override long Evaluate(Func<string, long> lookup) => lookup(Name);
        public override IEnumerable<string> Symbols => [Name];
    }
    private sealed record Unary(string Operator, Node Value) : Node
    {
        public override IEnumerable<string> Symbols => Value.Symbols;
        public override long Evaluate(Func<string, long> lookup) => Operator switch
        { "!" => Value.Evaluate(lookup) == 0 ? 1 : 0, "~" => ~Value.Evaluate(lookup), "-" => unchecked(-Value.Evaluate(lookup)), _ => Value.Evaluate(lookup) };
    }
    private sealed record Binary(string Operator, Node Left, Node Right) : Node
    {
        public override IEnumerable<string> Symbols => Left.Symbols.Concat(Right.Symbols);
        public override long Evaluate(Func<string, long> lookup)
        {
            var a = Left.Evaluate(lookup);
            if (Operator == "&&") return a != 0 && Right.Evaluate(lookup) != 0 ? 1 : 0;
            if (Operator == "||") return a != 0 || Right.Evaluate(lookup) != 0 ? 1 : 0;
            var b = Right.Evaluate(lookup);
            return Operator switch
            {
                "+" => unchecked(a + b), "-" => unchecked(a - b), "*" => unchecked(a * b),
                "/" => b != 0 && !(a == long.MinValue && b == -1) ? a / b : throw new InvalidOperationException("表达式除零或除法溢出。"),
                "%" => b != 0 && !(a == long.MinValue && b == -1) ? a % b : throw new InvalidOperationException("表达式取模无效。"),
                "<<" => b is >= 0 and < 64 ? a << (int)b : throw new InvalidOperationException("移位位数应在 0–63 之间。"),
                ">>" => b is >= 0 and < 64 ? a >> (int)b : throw new InvalidOperationException("移位位数应在 0–63 之间。"),
                "&" => a & b, "|" => a | b, "^" => a ^ b,
                "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0, "<" => a < b ? 1 : 0, ">" => a > b ? 1 : 0,
                "<=" => a <= b ? 1 : 0, ">=" => a >= b ? 1 : 0, _ => throw new InvalidOperationException("未知运算符。")
            };
        }
    }
    private readonly Node root;
    private DebugExpression(Node node) => root = node;
    public IReadOnlyList<string> Symbols => root.Symbols.Distinct(StringComparer.Ordinal).ToArray();
    public long Evaluate(Func<string, long> lookup) => root.Evaluate(lookup);
    public static DebugExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 256) throw new ArgumentException("请输入 1–256 字符的只读整数表达式。");
        return new(new Parser(expression).Read());
    }
    private sealed class Parser(string text)
    {
        private int index, tokens;
        private string current = "";
        private static int Precedence(string op) => op switch { "||" => 1, "&&" => 2, "|" => 3, "^" => 4, "&" => 5, "==" or "!=" => 6, "<" or "<=" or ">" or ">=" => 7, "<<" or ">>" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0 };
        public Node Read() { Next(); var node = ReadBinary(1, 0); if (current.Length > 0) throw Invalid(); return node; }
        private Node ReadBinary(int precedence, int depth)
        {
            var left = Primary(depth + 1);
            while (Precedence(current) >= precedence)
            {
                var op = current; Next(); left = new Binary(op, left, ReadBinary(Precedence(op) + 1, depth + 1));
            }
            return left;
        }
        private Node Primary(int depth)
        {
            if (depth > 32) throw new ArgumentException("表达式嵌套过深。");
            var value = current; Next();
            if (value == "(") { var inside = ReadBinary(1, depth + 1); if (current != ")") throw Invalid(); Next(); return inside; }
            if (value is "!" or "~" or "+" or "-") return new Unary(value, Primary(depth + 1));
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) throw Invalid();
                return new Constant(hex);
            }
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return new Constant(number);
            if (value is "true" or "false") return new Constant(value == "true" ? 1 : 0);
            if (value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_' || value[0] == '$' && value.Length > 1)) return new Symbol(value);
            throw Invalid();
        }
        private void Next()
        {
            if (++tokens > 160) throw new ArgumentException("表达式过于复杂。");
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) { current = ""; return; }
            var start = index++; var c = text[start];
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '$')
            { while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_')) index++; }
            else if (index < text.Length && text.Substring(start, 2) is "&&" or "||" or "==" or "!=" or "<=" or ">=" or "<<" or ">>" or "++" or "--") index++;
            current = text[start..index];
            if (current is "++" or "--" or "=" || c is '"' or '\'' or ';' or ',' or '{' or '}' or '[' or ']' or '.') throw Invalid();
        }
        private static ArgumentException Invalid() => new("表达式格式无效。支持整数、变量、$寄存器、括号及算术/比较/逻辑/位运算；不执行赋值或函数调用。");
    }
}

namespace StudioX.LabPlugins;

using System.Globalization;
using System.Numerics;

/// <summary>定宽整数：文本输入严格校验范围，运算结果按补码保留所选位宽。</summary>
internal sealed class ProgrammerWord(int width, int radix, bool signed)
{
    internal int Width { get; } = width;
    internal int Radix { get; } = radix;
    internal bool Signed { get; } = signed;
    internal BigInteger Modulus { get; } = BigInteger.One << width;
    internal ulong Mask => Width == 64 ? ulong.MaxValue : (1UL << Width) - 1;
    internal ulong Normalize(BigInteger value) => (ulong)(value & Mask);
    internal BigInteger Interpret(ulong value) => Signed ? SignedValue(value) : new BigInteger(value);
    internal BigInteger SignedValue(ulong value) => (value & (1UL << (Width - 1))) != 0 ? new BigInteger(value) - Modulus : new BigInteger(value);

    internal ulong Parse(string text)
    {
        var negative = text.StartsWith('-');
        if (text.StartsWith('-') || text.StartsWith('+'))
        {
            text = text[1..];
        }
        var radix = Radix;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            radix = 16;
            text = text[2..];
        }
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            radix = 2;
            text = text[2..];
        }
        else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            radix = 8;
            text = text[2..];
        }
        else if (text.StartsWith("0d", StringComparison.OrdinalIgnoreCase))
        {
            radix = 10;
            text = text[2..];
        }
        if (text.Length == 0 || text.StartsWith('_') || text.EndsWith('_') || text.Contains("__", StringComparison.Ordinal))
        {
            throw new ArgumentException("数字不能为空，分隔符 _ 只能放在数字之间。");
        }
        BigInteger value = 0;
        foreach (var ch in text)
        {
            if (ch == '_')
            {
                continue;
            }
            var digit = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix)
            {
                throw new ArgumentException($"数字“{ch}”不属于 {radix} 进制；可用 0x / 0d / 0o / 0b 指定进制。");
            }
            value = value * radix + digit;
            if (value > Mask)
            {
                throw new ArgumentException($"输入超过 {Width} 位范围，不会自动截断输入。");
            }
        }
        if (negative && value > (Modulus >> 1))
        {
            throw new ArgumentException($"负数超过 {Width} 位有符号范围。");
        }
        return Normalize(negative ? -value : value);
    }

    internal ulong Unary(string operation, ulong value) => operation switch
    {
        "+" => value,
        "-" => Normalize(-new BigInteger(value)),
        "~" or "not" => ~value & Mask,
        _ => throw new ArgumentException("未知一元运算。")
    };

    internal ulong Apply(string operation, ulong left, ulong right)
    {
        var a = Interpret(left);
        var b = Interpret(right);
        return operation switch
        {
            "+" => Normalize(a + b),
            "-" => Normalize(a - b),
            "*" => Normalize(a * b),
            "/" => b == 0 ? throw new ArgumentException("除数不能为 0。") : Normalize(a / b),
            "%" => b == 0 ? throw new ArgumentException("取余的除数不能为 0。") : Normalize(a % b),
            "&" or "and" => left & right,
            "|" or "or" => left | right,
            "^" or "xor" => left ^ right,
            "nand" => ~(left & right) & Mask,
            "nor" => ~(left | right) & Mask,
            "<<" or "shl" => Normalize(new BigInteger(left) << Shift()),
            ">>" or "shr" => Normalize(a >> Shift()),
            ">>>" => Normalize(new BigInteger(left) >> Shift()),
            "rol" => Rotate(true),
            "ror" => Rotate(false),
            _ => throw new ArgumentException("未知运算。")
        };
        int Shift()
        {
            // 不使用 C# 的隐式移位取模：移动完整位宽必须真的移出全部位。
            if (right > (ulong)Width)
            {
                throw new ArgumentException($"移位 / 循环移位数量须为 0–{Width}。");
            }
            return (int)right;
        }
        ulong Rotate(bool leftward)
        {
            var count = Shift() % Width;
            if (count == 0)
            {
                return left;
            }
            return leftward ? Normalize((new BigInteger(left) << count) | (left >> (Width - count))) :
                Normalize((left >> count) | (new BigInteger(left) << (Width - count)));
        }
    }

    internal static string Format(ulong value, int radix)
    {
        if (radix == 10)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        const string digits = "0123456789ABCDEF";
        var result = "";
        do
        {
            result = digits[(int)(value % (uint)radix)] + result;
            value /= (uint)radix;
        } while (value != 0);
        return result;
    }
}

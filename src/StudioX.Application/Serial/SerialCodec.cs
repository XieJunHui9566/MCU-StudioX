namespace StudioX.Application.Serial;

using System.Text;
using System.Text.RegularExpressions;

public enum SerialTextMode { Utf8, Gb2312, Hex }
public enum SerialLineEnding { None, CrLf, Lf, Cr }

public static partial class SerialCodec
{
    static SerialCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static Encoding EncodingFor(SerialTextMode mode, bool strict = false) => mode switch
    {
        SerialTextMode.Utf8 => new UTF8Encoding(false, strict),
        SerialTextMode.Gb2312 => Encoding.GetEncoding(936, strict ? EncoderFallback.ExceptionFallback : EncoderFallback.ReplacementFallback,
            strict ? DecoderFallback.ExceptionFallback : DecoderFallback.ReplacementFallback),
        _ => throw new ArgumentException("HEX 模式直接处理字节。")
    };
    public static byte[] Encode(string text, SerialTextMode mode, SerialLineEnding ending, bool escapes)
    {
        if (text.Length > 262144) throw new ArgumentException("发送输入过长；单次最多 64 KiB 数据。");
        byte[] bytes;
        if (mode == SerialTextMode.Hex)
        {
            var clean = Regex.Replace(text, @"0[xX]|[\s,;:_-]", "");
            if (clean.Length % 2 != 0 || !Regex.IsMatch(clean, @"\A[0-9a-fA-F]*\z")) throw new ArgumentException("HEX 必须是完整字节，例如：01 A5 00 FF；不接受单个半字节。");
            bytes = Convert.FromHexString(clean);
        }
        else bytes = EncodingFor(mode, true).GetBytes(escapes ? ExpandEscapes(text) : text);
        var suffix = ending switch { SerialLineEnding.CrLf => "\r\n", SerialLineEnding.Lf => "\n", SerialLineEnding.Cr => "\r", _ => "" };
        if (bytes.Length + suffix.Length > 65536) throw new ArgumentException("单次发送最多 64 KiB。");
        return [.. bytes, .. Encoding.ASCII.GetBytes(suffix)];
    }
    private static string ExpandEscapes(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i == text.Length) throw new ArgumentException("转义序列末尾缺少字符。");
            switch (text[i])
            {
                case '\\': result.Append('\\'); break;
                case 'r': result.Append('\r'); break;
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case '0': result.Append('\0'); break;
                case 'e': result.Append('\x1b'); break;
                case 'x':
                    if (i + 2 >= text.Length || !byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
                        throw new ArgumentException(@"\x 后需要两位十六进制数字。");
                    result.Append((char)value); i += 2; break;
                default: throw new ArgumentException(@"支持的转义：\r \n \t \0 \e \xHH \\");
            }
        }
        return result.ToString();
    }
}

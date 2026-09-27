using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Desktop;

/// <summary>
/// 执行实际逐行高亮，覆盖仅加载语法定义无法发现的零长度匹配和跨行状态错误。
/// </summary>
internal static class CodeHighlightingChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string pinMap = "SYSCLK 144000000\nBUSCLK 72000000\nHSECLK 8000000\n" +
            "# 系统频率与管脚映射，不得改变原始配置\n\n" +
            "PIN_1:INPUT led_input\nPIN_2:OUTPUT led_output # 行尾中文注释\n" +
            "PIN_3:INOUT shared_bus\nSYSCLK 144000000\n";
        check(CodeLanguage.ForFile("logic/pins.ve") == "AGM Pin Map", ".ve 选择 AGM 管脚和频率配置高亮");
        foreach (var dark in new[] { true, false })
        {
            var theme = dark ? "深色" : "浅色";
            var definition = CodeLanguage.Get("AGM Pin Map", dark)!;
            var document = new TextDocument(pinMap);
            using var highlighter = new DocumentHighlighter(document, definition);
            var lines = HighlightAll(highlighter, document);
            check(lines.Count == document.LineCount && lines.All(line => line.Sections.All(section => section.Length > 0)),
                $"{theme} .ve 普通行、空行、中文注释与行尾注释实际高亮均无零长度匹配");
            check(new[] { 1, 2, 3, 9 }.All(line => HasColor(lines[line - 1], "Keyword")) &&
                new[] { 1, 2, 3, 9 }.All(line => HasColor(lines[line - 1], "Number")),
                $"{theme} .ve SYSCLK、BUSCLK、HSECLK 和注释后频率行仍按配置高亮");
            check(HasColor(lines[3], "Comment") && !HasColor(lines[4], "Comment") &&
                HasColor(lines[6], "Comment") && !HasColor(lines[7], "Comment"),
                $"{theme} .ve 注释只覆盖本行，不污染下一行");
            check(new[] { 6, 7, 8 }.All(line => HasColor(lines[line - 1], "Type") &&
                HasColor(lines[line - 1], "Keyword")),
                $"{theme} .ve PIN_ 管脚与 INPUT、OUTPUT、INOUT 可识别");
            check(document.Text == pinMap, $"{theme} .ve 高亮不改写频率、管脚和中文文本");

            // 同类注释前缀必须执行匹配；构造高亮定义本身不会检查匹配长度。
            CheckComment(check, dark, "C", "int value; // 中文注释\n/* 跨行\n注释 */ int next;\n", theme);
            CheckComment(check, dark, "CMake", "set(value 1) # 中文注释\n#[[跨行\n注释]]\nset(next 2)\n", theme);
            CheckComment(check, dark, "Assembly", "addi a0, a0, 1 # 中文注释\nret ; 行尾注释\n", theme);
            CheckComment(check, dark, "Verilog", "wire value; // 中文注释\n/* 跨行\n注释 */ assign value = 1'b1;\n", theme);
        }
    }

    private static List<HighlightedLine> HighlightAll(DocumentHighlighter highlighter, TextDocument document)
    {
        var lines = new List<HighlightedLine>(document.LineCount);
        for (var line = 1; line <= document.LineCount; line++)
        {
            lines.Add(highlighter.HighlightLine(line));
        }
        return lines;
    }

    private static bool HasColor(HighlightedLine line, string color) =>
        line.Sections.Any(section => section.Color.Name == color);

    private static void CheckComment(Action<bool, string> check, bool dark, string language, string text, string theme)
    {
        var definition = CodeLanguage.Get(language, dark)!;
        var document = new TextDocument(text);
        using var highlighter = new DocumentHighlighter(document, definition);
        var lines = HighlightAll(highlighter, document);
        check(lines.Any(line => HasColor(line, "Comment")) &&
            lines.All(line => line.Sections.All(section => section.Length > 0)),
            $"{theme} {language} 注释执行真实高亮且无零长度匹配");
    }
}

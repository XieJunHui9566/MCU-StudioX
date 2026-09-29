namespace StudioX.Application;

using System.Text.RegularExpressions;

/// <summary>为常见工具诊断提供离线中文释义；原始消息始终由调用者原样保留。</summary>
public static class DiagnosticText
{
    public const string Untranslated = "此诊断暂未收录中文释义，请查看下方工具原文。";
    private static readonly (Regex Pattern, string Chinese)[] Rules =
    [
        Rule(@"^Included header (.+) is not used directly$", "当前文件未直接使用头文件“$1”。"),
        Rule(@"^Use of undeclared identifier '(.+)'$", "使用了尚未声明的标识符“$1”。"),
        Rule(@"^Use of undeclared identifier '(.+)'; did you mean '(.+)'\?$", "使用了尚未声明的标识符“$1”；是否想使用“$2”？"),
        Rule(@"^'(.+)' undeclared(?: \(first use in this function\))?$", "标识符“$1”尚未声明。"),
        Rule(@"^'(.+)' undeclared(?: \(first use in this function\))?; did you mean '(.+)'\?$", "标识符“$1”尚未声明；是否想使用“$2”？"),
        Rule(@"^'(.+)' was not declared in this scope$", "当前作用域中未声明“$1”。"),
        Rule(@"^Unknown type name '(.+)'$", "未知的类型名称“$1”。"),
        Rule(@"^Unknown type name '(.+)'; did you mean '(.+)'\?$", "未知的类型名称“$1”；是否想使用“$2”？"),
        Rule(@"^'(.+)' file not found$", "找不到头文件“$1”。"),
        Rule(@"^(.+): No such file or directory$", "找不到文件或目录“$1”。"),
        Rule(@"^Expected '(.+)' after expression$", "表达式后缺少“$1”。"),
        Rule(@"^Expected '(.+)' after declaration$", "声明后缺少“$1”。"),
        Rule(@"^Expected '(.+)' after return statement$", "return 语句后缺少“$1”。"),
        Rule(@"^Expected '(.+)' after top level declarator$", "顶层声明后缺少“$1”。"),
        Rule(@"^Expected '(.+)' after (.+)$", "在 $2 后缺少“$1”。"),
        Rule(@"^Expected '(.+)' before (.+)$", "在 $2 前缺少“$1”。"),
        Rule(@"^Expected '(.+)'$", "此处应为“$1”。"),
        Rule(@"^Expected expression$", "此处缺少有效的表达式。"),
        Rule(@"^Expected identifier or '(.+)'$", "此处应为标识符或“$1”。"),
        Rule(@"^Expected identifier$", "此处缺少有效的标识符。"),
        Rule(@"^Expected declaration or statement at end of input$", "文件末尾的声明或语句不完整。"),
        Rule(@"^Unused variable '(.+)'$", "变量“$1”未被使用。"),
        Rule(@"^Unused parameter '(.+)'$", "参数“$1”未被使用。"),
        Rule(@"^Unused function '(.+)'$", "函数“$1”未被使用。"),
        Rule(@"^Unused label '(.+)'$", "标签“$1”未被使用。"),
        Rule(@"^'(.+)' defined but not used$", "“$1”已定义，但未被使用。"),
        Rule(@"^Variable '(.+)' set but not used$", "变量“$1”已赋值，但其值未被使用。"),
        Rule(@"^'(.+)' is uninitialized when used here$", "此处使用的“$1”尚未初始化。"),
        Rule(@"^'(.+)' (?:is used|may be used) uninitialized(?: in this function)?$", "“$1”在使用前可能未初始化。"),
        Rule(@"^Implicit declaration of function '(.+)'$", "调用函数“$1”之前没有可见的函数声明。"),
        Rule(@"^Call to undeclared function '(.+)'; ISO C99 and later do not support implicit function declarations$", "调用了尚未声明的函数“$1”；ISO C99 及后续标准不支持隐式函数声明。"),
        Rule(@"^Redefinition of '(.+)'$", "“$1”被重复定义。"),
        Rule(@"^Conflicting types for '(.+)'$", "“$1”的类型与先前声明冲突。"),
        Rule(@"^No member named '(.+)' in '(.+)'$", "类型“$2”中没有名为“$1”的成员。"),
        Rule(@"^Too few arguments to function call, expected (\d+), have (\d+)$", "函数调用缺少参数：需要 $1 个，实际提供 $2 个。"),
        Rule(@"^Too many arguments to function call, expected (\d+), have (\d+)$", "函数调用参数过多：需要 $1 个，实际提供 $2 个。"),
        Rule(@"^Too few arguments to function '(.+)'$", "调用函数“$1”时提供的参数太少。"),
        Rule(@"^Too many arguments to function '(.+)'$", "调用函数“$1”时提供的参数太多。"),
        Rule(@"^Incompatible pointer types passing '(.+)' to parameter of type '(.+)'$", "传入的指针类型“$1”与参数类型“$2”不兼容。"),
        Rule(@"^Incompatible pointer types assigning to '(.+)' from '(.+)'$", "不能将“$2”类型的指针赋给“$1”类型的指针。"),
        Rule(@"^Incompatible integer to pointer conversion passing '(.+)' to parameter of type '(.+)'$", "将整数类型“$1”传给指针参数“$2”时发生不兼容的转换。"),
        Rule(@"^Non-void function does not return a value(?: in all control paths)?$", "非 void 函数存在未返回值的执行路径。"),
        Rule(@"^Control reaches end of non-void function$", "非 void 函数执行到末尾时没有返回值。"),
        Rule(@"^Division by zero(?: is undefined)?$", "除数为零。"),
        Rule(@"^Comparison of integers of different signs: '(.+)' and '(.+)'$", "比较的整数符号性不同：“$1”与“$2”。"),
        Rule(@"^Comparison of integer expressions of different signedness: '(.+)' and '(.+)'$", "比较的整数符号性不同：“$1”与“$2”。"),
        Rule("""^Unknown CMake command(?: "(.+)")?\.?$""", "使用了未知的 CMake 命令。"),
        Rule(@"^#error (.+)$", "代码中的 #error 指令报告：$1"),
        Rule(@"^#warning (.+)$", "代码中的 #warning 指令报告：$1")
    ];
    private static readonly Regex WarningFlag = new(@"\s+\[-W[^\]\r\n]+\]$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string ChineseSummary(string message)
    {
        // 诊断来自外部工具。限制释义匹配长度，但不能截断用于显示、复制和排查的原文。
        if (message.Length > 8192)
        {
            return Untranslated;
        }
        var primary = message.Split('\n', 2)[0].Trim();
        var fixAvailable = primary.EndsWith(" (fix available)", StringComparison.OrdinalIgnoreCase);
        if (fixAvailable)
        {
            primary = primary[..^16];
        }
        primary = WarningFlag.Replace(primary, "");
        foreach (var (pattern, chinese) in Rules)
        {
            var match = pattern.Match(primary);
            if (match.Success)
            {
                return match.Result(chinese) + (fixAvailable ? " 可快速修复。" : "");
            }
        }
        return Untranslated + (fixAvailable ? " 工具提供快速修复。" : "");
    }

    public static string Bilingual(string message) => ChineseSummary(message) + "\n" + message;

    private static (Regex, string) Rule(string pattern, string chinese) =>
        (new Regex(pattern.Replace("'", "['‘’]", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), chinese);
}

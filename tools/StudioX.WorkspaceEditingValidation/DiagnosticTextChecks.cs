using StudioX.Application;

internal static class DiagnosticTextChecks
{
    public static void Run(Action<bool, string> check)
    {
        var warning = "Included header alta.h is not used directly (fix available)";
        check(DiagnosticText.ChineseSummary(warning) == "当前文件未直接使用头文件“alta.h”。 可快速修复。", "unused umbrella header explained without recommending automatic removal");
        check(DiagnosticText.Bilingual(warning).EndsWith("\n" + warning, StringComparison.Ordinal), "diagnostic original including fix annotation preserved verbatim");
        check(DiagnosticText.ChineseSummary("Use of undeclared identifier 'LED1'; did you mean 'LED2'?").Contains("是否想使用“LED2”", StringComparison.Ordinal), "identifier suggestion translated without dropping replacement name");
        check(DiagnosticText.ChineseSummary("unused variable ‘计数值’ [-Wunused-variable]") == "变量“计数值”未被使用。", "GCC typographic quotes and warning code handled");
        check(DiagnosticText.ChineseSummary("Included header drivers/$gpio[1].h is not used directly").Contains("drivers/$gpio[1].h", StringComparison.Ordinal), "file names containing regex and replacement syntax remain literal");
        var multiline = "'device.h' file not found\r\n  included from \"board.h\":6";
        check(DiagnosticText.Bilingual(multiline) == "找不到头文件“device.h”。\n" + multiline, "multiline tool context retained alongside translated primary diagnostic");
        check(DiagnosticText.ChineseSummary("Expected ';' after expression") == "表达式后缺少“;”。", "missing delimiter translated");
        check(DiagnosticText.ChineseSummary("Too few arguments to function call, expected 2, have 1").Contains("需要 2 个，实际提供 1 个", StringComparison.Ordinal), "argument counts preserved in Chinese explanation");
        var unknown = "vendor diagnostic: register window is special";
        check(DiagnosticText.Bilingual(unknown) == DiagnosticText.Untranslated + "\n" + unknown, "unknown vendor diagnostics use explicit fallback instead of guessed translation");
        var longMessage = new string('x', 20_000);
        check(DiagnosticText.Bilingual(longMessage) == DiagnosticText.Untranslated + "\n" + longMessage, "long external diagnostic bypasses matching without losing original content");
    }
}

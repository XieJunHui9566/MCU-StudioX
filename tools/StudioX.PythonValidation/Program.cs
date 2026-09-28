using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Application.CodeIntelligence;
using StudioX.Desktop;

var service = new PythonAssistanceService();
var passed = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new InvalidOperationException(name);
    }
    passed.Add("PASS: " + name);
}
Task<PythonAssistance> Assist(string marked)
{
    var caret = marked.IndexOf('§');
    return service.GetAsync("中文目录/未保存.py", marked.Replace("§", ""), caret);
}
foreach (var path in new[] { "main.py", "MAIN.PYW", "接口.pyi" })
{
    Check(PythonAssistanceService.Supports(path) && CodeLanguage.ForFile(path) == "Python", "文件识别 " + path);
}
Check(!PythonAssistanceService.Supports("main.c") && !PythonAssistanceService.Supports("main.py.txt"), "不改变其他文件的语言归属");
Check((await Assist("pri§")).Suggestions.Any(item => item.Label == "print" && item.Kind == 3), "内置函数提示");
Check((await Assist("ret§")).Suggestions.Any(item => item.Label == "return" && item.Kind == 14), "关键字提示");
Check(!(await Assist("Pri§")).Suggestions.Any(item => item.Label == "print"), "Python 大小写敏感");
Check((await Assist("采样值 = 10\n采§")).Suggestions.Any(item => item.Label == "采样值"), "中文标识符与未保存变量");
Check((await Assist("café = 10\ncaf§")).Suggestions.Any(item => item.Label == "café"), "组合字符标识符");
Check(!(await Assist("# secret_name\nvalue = 'hidden_name'\nsec§")).Suggestions.Any(item => item.Label == "secret_name"), "注释内容不会污染符号补全");
foreach (var marked in new[]
{
    "# pri§", "value = 'pri§'", "value = \"pri§\"", "value = r'\\pri§'",
    "value = '''first\npri§\nlast'''", "value = f\"{len(\"pri§\")}\"",
    "value = f\"{{literal}} {pri§}\"", "value = t\"{pri§}\"",
    "value = 'first\\\npri§'", "value = 'first\\\r\npri§'", "value = '''unclosed\npri§"
})
{
    var result = await Assist(marked);
    Check(result.Suggestions.Count == 0 && result.Signature is null, "注释或字符串内抑制提示 " + marked.Replace("\n", "\\n").Replace("\r", "\\r"));
}
Check((await Assist("# comment\r\npri§")).Suggestions.Any(item => item.Label == "print"), "CRLF 注释结束后恢复补全");
Check((await Assist("text = '''closed'''\npri§")).Suggestions.Any(item => item.Label == "print"), "三引号结束后恢复补全");
Check((await Assist("text = 'broken\npri§")).Suggestions.Any(item => item.Label == "print"), "未结束单行字符串不污染后续行");
Check((await Assist("obj.pr§")).Suggestions.Count == 0, "未知对象不伪造成员补全");
Check((await Assist("number = 123§")).Suggestions.Count == 0, "数值不触发标识符补全");
var middle = await Assist("# 注释\r\nprint§ln");
var print = middle.Suggestions.Single(item => item.Label == "print");
Check(print.Range is { Start.Line: 1, Start.Character: 0, End.Line: 1, End.Character: 7 }, "CRLF 词中补全覆盖完整旧词");
Check((await Assist("from library import pri§")).Suggestions.Count == 0, "不使用内置函数伪造模块导出名称");
Check((await Assist("import\tpri§")).Suggestions.Count == 0, "制表符分隔的导入上下文");
Check((await Assist("from library import (\n    pri§\n)")).Suggestions.Count == 0, "括号多行导入上下文");
Check((await Assist("from library import \\\n    pri§")).Suggestions.Count == 0, "反斜杠续行导入上下文");
Check((await Assist("import os; pri§")).Suggestions.Any(item => item.Label == "print"), "导入语句结束后恢复普通补全");
Check((await Assist("print = 1\nprint(§")).Signature is null, "可见赋值覆盖同名内置函数提示");
Check((await Assist("mat§")).Suggestions.Any(item => item.Label == "match"), "模式匹配软关键字提示");
const string declaration = "async def sample(a: int, b=(1, 2), *, scale=3):\n    return a\n\n";
var functions = await Assist(declaration + "sam§");
Check(functions.Suggestions.Any(item => item.Label == "sample" && item.Kind == 3), "当前文件 async 函数提示");
var call = await Assist(declaration + "sample([1, 2], §");
Check(call.Signature is { ActiveParameter: 1, Parameters.Count: 3 } && call.Signature.Parameters[1] == "b=(1, 2)", "嵌套默认值与调用参数计数");
Check((await Assist(declaration + "sample(scale=§")).Signature?.ActiveParameter == 2, "关键字参数定位");
Check((await Assist("print(1, 2, §")).Signature?.ActiveParameter == 0, "可变参数不误定位到关键字参数");
Check((await Assist("print(end=§")).Signature?.ActiveParameter == 2, "内置函数关键字参数");
Check((await Assist("print(len(§")).Signature?.Label.StartsWith("len(") == true, "内层调用参数提示");
Check((await Assist("print(len([]), §")).Signature?.Label.StartsWith("print(") == true, "内层调用结束恢复外层提示");
Check((await Assist("obj.print(§")).Signature is null, "未知成员不借用同名内置函数签名");
Check((await Assist("def print(§")).Signature is null, "定义参数时不提示同名内置函数");
Check((await Assist("class Sensor:\n    pass\nSen§")).Suggestions.Any(item => item.Label == "Sensor" && item.Kind == 7), "当前文件类提示");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await service.GetAsync("test.py", "print", 5, cancellation.Token);
        throw new InvalidOperationException("取消请求仍成功返回");
    }
    catch (OperationCanceledException)
    {
        Check(true, "取消请求不返回过期结果");
    }
}
Check((await service.GetAsync("test.py", new string('x', 1024 * 1024 + 1), 0)).Suggestions.Count == 0, "超限文本保护");

const string python = "@staticmethod\nasync def read_value(count: int = 0x1F):\n    # 中文注释\n    text = r'''first\n    # this is a string\n    last'''\n    value = 1_000 + .5e-2 + 0b_101 + 0o77 + 2j\n    return f\"value={value}\"\n";
foreach (var dark in new[] { false, true })
{
    var document = new TextDocument(python);
    using var highlighter = new DocumentHighlighter(document, CodeLanguage.Get("Python", dark)!);
    var lines = Enumerable.Range(1, document.LineCount).Select(highlighter.HighlightLine).ToArray();
    Check(lines.All(line => line.Sections.All(section => section.Length > 0)), $"高亮无零长度匹配 dark={dark}");
    Check(lines[0].Sections.Any(section => section.Color.Name == "Macro"), "装饰器高亮");
    Check(lines[1].Sections.Any(section => section.Color.Name == "Keyword") && lines[1].Sections.Any(section => section.Color.Name == "Function"), "async/def/函数高亮");
    Check(lines[2].Sections.Any(section => section.Color.Name == "Comment"), "中文注释高亮");
    Check(lines[4].Sections.All(section => section.Color.Name == "String") && lines[4].Sections.Count > 0, "三引号内 # 按字符串高亮");
    Check(lines[6].Sections.Count(section => section.Color.Name == "Number") == 5 && !lines[6].Sections.Any(section => section.Color.Name == "String"), "Python 数字与三引号状态恢复");
    Check(document.Text == python, "高亮不改写文本");
    foreach (var language in new[] { "C", "C++", "CMake", "Verilog", "AGM Pin Map", "Devicetree" })
    {
        using var regression = new DocumentHighlighter(new TextDocument("# comment\nint main(void) { return 1; }\n"), CodeLanguage.Get(language, dark)!);
        Check(regression.HighlightLine(2).Sections.All(section => section.Length > 0), "既有语言高亮回归 " + language);
    }
}
foreach (var line in passed)
{
    Console.WriteLine(line);
}
Console.WriteLine($"Python offline checks passed: {passed.Count}");
if (args is [var output])
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllLinesAsync(output, passed);
}

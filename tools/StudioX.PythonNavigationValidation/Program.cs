using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Packages;

if (args is not [var output])
{
    throw new ArgumentException("Use a new output directory.");
}
var root = Path.GetFullPath(output);
if (Directory.Exists(root))
{
    throw new ArgumentException("Output exists.");
}
Directory.CreateDirectory(Path.Combine(root, "lib"));
var results = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
    results.Add("PASS: " + name);
    Console.WriteLine(results[^1]);
}
const string source = "from machine import Pin\nfrom helper import blink as pulse\nimport helper as h\nled = Pin('LED', Pin.OUT)\nled.toggle()\npulse(led)\nh.blink(led)\nvalue = 1\ndef local(value):\n    return value\nprint(value)\n# value pulse led\ns = 'value pulse led'\n";
const string helper = "def blink(pin):\n    pin.on()\n    return pin\n";
await File.WriteAllTextAsync(Path.Combine(root, "main.py"), source);
await File.WriteAllTextAsync(Path.Combine(root, "lib/helper.py"), helper);
var service = new PythonNavigationService();
var profile = new MicroPythonProfile("RPI_PICO", "1.29.0");
Task<IReadOnlyList<CodeLocation>> Find(string marker, bool references = false, IReadOnlyList<CodeDocumentSnapshot>? buffers = null) =>
    service.FindAsync(root, "main.py", source, source.IndexOf(marker, StringComparison.Ordinal), references, buffers, profile);
var pin = await Find("Pin('LED'");
Check(pin.Count == 1 && pin[0].DocumentPath == "@micropython/machine.pyi", "Pin 转到只读板级 API 声明");
var api = await service.ReadAsync(root, pin[0].DocumentPath, profile);
Check(api.IsReadOnly && api.Text.Contains("class Pin:", StringComparison.Ordinal), "API 文档明确只读且包含类声明");
var method = await Find("toggle()");
Check(method.Count == 1 && api.Text.Split('\n')[method[0].Range.Start.Line].Contains("def toggle", StringComparison.Ordinal), "直接构造实例的方法导航");
var imported = await Find("pulse(led)");
Check(imported.Count == 1 && imported[0].DocumentPath == "lib/helper.py" && imported[0].Range.Start.Line == 0, "从 lib 模块导入别名跳转");
Check((await Find("blink(led)")).Single().DocumentPath == "lib/helper.py", "import 模块别名成员跳转");
var shadow = await Find("value\nprint");
Check(shadow.Count == 1 && shadow[0].Range.Start.Line == 8, "函数参数遮蔽全局同名变量");
var global = await Find("value)\n#");
Check(global.Count == 1 && global[0].Range.Start.Line == 7, "模块变量导航");
var refs = await Find("value)\n#", true);
Check(refs.Count == 2 && refs.All(item => item.Range.Start.Line is 7 or 10), "引用排除同名参数、注释和字符串");
var cross = await Find("pulse(led)", true);
Check(cross.Any(item => item.DocumentPath == "lib/helper.py") && cross.Count(item => item.DocumentPath == "main.py") == 4, "跨文件函数引用包含导入与别名调用");
var dirty = await Find("pulse(led)", buffers: [new("lib/helper.py", "# 未保存\n" + helper)]);
Check(dirty.Single().Range.Start.Line == 1, "未保存的其他标签快照优先于磁盘");
Check((await Find("value pulse led")).Count == 0, "注释不产生导航结果");
Check((await service.FindAsync(root, "main.py", "unknown.call()", 8, false, profile: profile)).Count == 0, "不猜测未知对象成员");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    try
    {
        await service.FindAsync(root, "main.py", source, 0, true, token: cancel.Token);
        throw new InvalidOperationException("Cancellation ignored");
    }
    catch (OperationCanceledException) { Check(true, "导航可取消"); }
}
Directory.CreateDirectory(Path.Combine(root, "pkg"));
await File.WriteAllTextAsync(Path.Combine(root, "pkg/__init__.py"), "");
await File.WriteAllTextAsync(Path.Combine(root, "pkg/sensor.py"), "def read():\n    return 1\n");
const string relative = "from .sensor import read\nread()\n";
await File.WriteAllTextAsync(Path.Combine(root, "pkg/app.py"), relative);
Check((await service.FindAsync(root, "pkg/app.py", relative, relative.LastIndexOf("read", StringComparison.Ordinal), false)).Single().DocumentPath == "pkg/sensor.py", "包内相对导入导航");
const string nesting = "x = 0\ndef outer():\n    x = 1\n    def inner():\n        nonlocal x\n        return x\n    return x\ndef another():\n    global x\n    return x\n";
await File.WriteAllTextAsync(Path.Combine(root, "scope.py"), nesting);
Check((await service.FindAsync(root, "scope.py", nesting, nesting.IndexOf("x\n    return", StringComparison.Ordinal), false)).Single().Range.Start.Line == 2, "nonlocal 指向外层函数绑定");
Check((await service.FindAsync(root, "scope.py", nesting, nesting.LastIndexOf('x'), false)).Single().Range.Start.Line == 0, "global 指向模块绑定");

Check(TextSearchService.Find("Pin pin pinMode", new("pin")).Count == 3, "普通查找默认忽略大小写");
Check(TextSearchService.Find("Pin pin pinMode", new("pin", MatchCase: true)).Count == 2, "区分大小写");
Check(TextSearchService.Find("pin pinMode _pin pin_", new("pin", WholeWord: true)).Count == 1, "全词包含标识符下划线边界");
Check(TextSearchService.Find("中文 中文名", new("中文", WholeWord: true)).Count == 1, "Unicode 全词边界");
Check(TextSearchService.Find("a.b aXb", new("a.b")).Count == 1, "普通模式转义正则字符");
Check(TextSearchService.Find("x x", new("x"), "$1").All(item => item.Replacement == "$1"), "普通替换保留美元文本");
var captures = TextSearchService.Find("a1 a2", new(@"a(\d)", RegularExpression: true), "b$1");
Check(captures.Select(item => item.Replacement).SequenceEqual(new[] { "b1", "b2" }), "正则捕获组替换");
Check(TextSearchService.Find("one\ntwo", new("^", RegularExpression: true), "> ").Count == 2, "零长度匹配可遍历且支持多行行首");
Check(TextSearchService.Find("x", new("")).Count == 0, "空查找不产生全文件替换");
try
{
    TextSearchService.Find("text", new("[", RegularExpression: true));
    throw new InvalidOperationException("Invalid regex accepted");
}
catch (ArgumentException) { Check(true, "非法正则返回错误而不产生替换计划"); }
try
{
    TextSearchService.Find("text", new("[", WholeWord: true, RegularExpression: true));
    throw new InvalidOperationException("Whole-word wrapper changed regex validity");
}
catch (ArgumentException) { Check(true, "全词模式不得把非法正则意外补全"); }
await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results);
Console.WriteLine("PASS " + results.Count);

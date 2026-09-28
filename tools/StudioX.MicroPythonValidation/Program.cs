using System.Security.Cryptography;
using System.Text;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.MicroPython;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

if (args is not [var packsRoot, var python, var output])
{
    Console.Error.WriteLine("Usage: <RaspberryPi-MicroPython packs directory> <local Python executable> <new-output-directory>");
    return 2;
}
var root = Path.GetFullPath(output);
if (Directory.Exists(root)) { throw new InvalidOperationException("Output must be new"); }
Directory.CreateDirectory(root);
var results = new List<string>();
void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
    results.Add("PASS: " + message);
    Console.WriteLine(results[^1]);
}
async Task Reject(Func<Task> action, string code, string description)
{
    try
    {
        await action();
        throw new InvalidOperationException("未拒绝：" + description);
    }
    catch (StudioXException error) when (error.Code == code) { Check(true, description); }
}
var repository = new PackRepository(Path.Combine(root, "packs"));
var assistance = new PythonAssistanceService();
foreach (var chip in new[] { "RP2040", "RP2350" })
{
    var pack = await repository.ImportAsync(Path.Combine(packsRoot, chip + "-0.2.0", "raspberrypi." + chip.ToLowerInvariant() + "-0.2.0.mcupack"));
    var device = pack.Manifest.Devices.Single();
    Check(device.Templates.Count(item => item.MicroPython is not null) == 2 && device.Templates.Count(item => item.MicroPython is null) == 3, chip + " 独立包保留三种 C 模板并增加两种 MicroPython 模板");
    foreach (var template in device.Templates)
    {
        var destination = Path.Combine(root, "中文 工程", chip + "-" + template.Id);
        var project = await new ProjectService().CreateAsync(pack, device.Id, template.Id, "validation", destination);
        await ProjectService.ReadAsync(destination);
        if (template.MicroPython is null)
        {
            Check(project.Kind == ProjectKind.Pack && File.Exists(Path.Combine(destination, "src/main.c")) && File.Exists(Path.Combine(destination, "CMakeLists.txt")), chip + " C 工程生成回归 " + template.Id);
            continue;
        }
        Check(project.Kind == ProjectKind.MicroPython && File.Exists(Path.Combine(destination, "main.py")) && File.Exists(Path.Combine(destination, "boot.py")) &&
            !File.Exists(Path.Combine(destination, "CMakeLists.txt")) && !Directory.Exists(Path.Combine(destination, "device/sdk")), chip + " 脚本入口与工程隔离 " + template.Id);
        var tools = new ToolsetCatalog(Path.Combine(root, "absent-tools"));
        await Reject(() => new BuildService(tools).BuildAsync(destination), "MICROPYTHON_NATIVE_OPERATION", "禁止 C 编译 " + chip);
        Check(await new OpenOcdService(tools).ConfigurationAsync(destination) is null, "禁止 OpenOCD 下载 " + chip);
        await Reject(() => HardwareDebugPreparer.PrepareAsync(destination, new OpenOcdService(tools)), "MICROPYTHON_NATIVE_OPERATION", "禁止 GDB 准备 " + chip);
        if (template.Id != "micropython-minimal")
        {
            continue;
        }
        var selected = project.MicroPython!;
        async Task<PythonAssistance> Hints(string text) => await assistance.GetAsync("main.py", text, text.Length, microPython: selected);
        Check((await Hints("import ma")).Suggestions.Any(item => item.Label == "machine" && item.Kind != 3), "MicroPython 模块导入提示 " + chip);
        Check((await Hints("from machine import P")).Suggestions.Any(item => item.Label == "Pin" && item.Kind != 3), "导入 Pin 不插入调用括号");
        Check((await Hints("import machine as m\nm.P")).Suggestions.Any(item => item.Label == "Pin"), "模块别名补全");
        Check((await Hints("from machine import Pin as P\nled = P('LED', P.OUT)\nled.t")).Suggestions.Any(item => item.Label == "toggle"), "直接构造对象成员补全");
        Check((await Hints("from machine import Pin\nPin.O")).Suggestions.Any(item => item.Label == "OUT"), "Pin 常量补全");
        Check((await Hints("import time\ntime.sleep_ms(")).Signature?.Label.StartsWith("time.sleep_ms(") == true, "端口 API 参数提示");
        Check((await Hints("import machine\n# machine.P")).Suggestions.Count == 0, "MicroPython 注释抑制");
        Check((await assistance.GetAsync("main.py", "import machine\nmachine.P", 24)).Suggestions.Count == 0, "通用 Python 不混入板级 API");

        var deviceRoot = Path.Combine(root, chip + "-simulated-filesystem");
        FixtureTransport? transport = null;
        await using var hub = new DeviceHub();
        await using var session = new MicroPythonSessionService(hub, _ => transport = new(python, deviceRoot, selected));
        await session.ConnectAsync(destination, "COM77");
        Check(session.IsConnected && transport!.IsSimulated, "模拟 raw REPL 握手与身份检查 " + chip);
        var executed = await session.ExecuteAsync("print('中文输出')");
        Check(executed.Output.Trim() == "中文输出" && executed.Error == "", "碎片 UTF-8 输出重组");
        var errorResult = await session.ExecuteAsync("raise ValueError('保留诊断')");
        Check(errorResult.Error.Contains("ValueError: 保留诊断", StringComparison.Ordinal) && session.IsConnected, "保留解释器原始错误且帧边界有效");
        await Reject(async () => { await hub.OpenAsync(new FixtureTransport(python, deviceRoot, selected)); }, "DEVICE_OWNED", "单一设备连接所有者");
        var oldBytes = Encoding.UTF8.GetBytes("print('旧程序')\n");
        await File.WriteAllBytesAsync(Path.Combine(deviceRoot, "main.py"), oldBytes);
        var bytes = Encoding.UTF8.GetBytes("# 中文脚本\n" + string.Concat(Enumerable.Repeat("print('sample')\n", 100)));
        var uploaded = await session.UploadAsync("main.py", bytes);
        Check(File.ReadAllBytes(Path.Combine(deviceRoot, "main.py")).SequenceEqual(bytes) && uploaded.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "分块上传及板端 SHA-256 校验");
        Check(uploaded.BackupPath is not null && File.ReadAllBytes(uploaded.BackupPath).SequenceEqual(oldBytes), "同名旧脚本真实备份且内容一致");
        Check(!transport!.Commands.Any(command => command.Contains("exec(open(", StringComparison.Ordinal)), "上传不自动执行入口");
        var newFile = await session.UploadAsync("lib/测试.py", "VALUE = 1\n"u8.ToArray());
        Check(newFile.BackupPath is null && File.Exists(Path.Combine(deviceRoot, "lib/测试.py")), "嵌套目录与 Unicode 路径上传");
        var script = "print(__name__, __file__, '中文')\ndef answer():\n return 42\nprint(answer())\n";
        await session.UploadAsync("lib/运行.py", Encoding.UTF8.GetBytes(script));
        var runOutput = new StringBuilder();
        var runSucceeded = await session.RunScriptAsync("lib/运行.py", text => runOutput.Append(text));
        Check(runSucceeded && runOutput.ToString().Contains("__main__ lib/运行.py 中文", StringComparison.Ordinal) && runOutput.ToString().Contains("42", StringComparison.Ordinal), "开始运行指定板上脚本并保留主模块与路径语义：" + runOutput);
        Check(File.ReadAllText(Path.Combine(deviceRoot, "lib/运行.py")) == script && session.IsConnected, "运行不重写板上文件且正常完成后可复用连接");
        runOutput.Clear();
        await session.UploadAsync("failure.py", "raise ValueError('脚本异常')\n"u8.ToArray());
        Check(!await session.RunScriptAsync("failure.py", text => runOutput.Append(text)) && runOutput.ToString().Contains("failure.py", StringComparison.Ordinal) && runOutput.ToString().Contains("ValueError: 脚本异常", StringComparison.Ordinal), "脚本异常原文与真实文件名实时交付");
        Check((await session.ExecuteAsync("print('still ready')")).Output.Contains("still ready", StringComparison.Ordinal), "脚本异常后结束帧完整，REPL 可继续使用");
        runOutput.Clear();
        Check(!await session.RunScriptAsync("missing.py", text => runOutput.Append(text)) && runOutput.ToString().Contains("missing.py", StringComparison.Ordinal), "缺失脚本报告原始错误而非运行成功");
        await Reject(() => session.RunScriptAsync("../main.py", _ => { }), "MICROPYTHON_PATH", "运行拒绝越界脚本路径");

        transport!.RunForeverNext = true;
        var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveOutput = new StringBuilder();
        long received = 0;
        var running = session.RunScriptAsync("main.py", text =>
        {
            Interlocked.Add(ref received, text.Length);
            if (liveOutput.Length < 100) { liveOutput.Append(text); }
            if (liveOutput.ToString().Contains("中文持续输出", StringComparison.Ordinal)) { firstOutput.TrySetResult(); }
        });
        await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!running.IsCompleted && liveOutput.ToString() == "中文持续输出", "未结束且无换行的程序也实时显示分帧中文输出");
        for (var batch = 0; batch < 300; batch++)
        {
            transport.StreamOutput(new string('x', 4096));
            await Task.Delay(2);
        }
        if (chip == "RP2040") { await Task.Delay(TimeSpan.FromSeconds(31)); }
        Check(!running.IsCompleted && Interlocked.Read(ref received) > 1024 * 1024,
            chip == "RP2040" ? "持续运行超过 30 秒、累计输出超过 1 MiB 仍保持会话 RP2040" : "累计输出超过 1 MiB 仍保持会话 RP2350");
        if (chip == "RP2040")
        {
            transport.FinishRun();
            Check(await running.WaitAsync(TimeSpan.FromSeconds(3)), "持续运行最终正常退出并解析结束帧");
            transport.RunForeverNext = true;
            running = session.RunScriptAsync("main.py", _ => { });
            await Task.Delay(150);
        }
        var interrupts = transport.Interrupts;
        await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try { await running; throw new InvalidOperationException("运行未取消"); }
        catch (OperationCanceledException)
        {
            Check(!session.IsConnected && transport.Closed && transport.Interrupts > interrupts, "停止持续运行发送中断并释放唯一端口");
        }
        await session.ConnectAsync(destination, "COM77");
        Check((await session.ExecuteAsync("print('reconnected')")).Output.Contains("reconnected", StringComparison.Ordinal), "运行停止后可重新连接执行");
        foreach (var path in new[] { "../main.py", "/main.py", "x\\main.py", ".studiox/a.py", "lib//x.py", "main.bin" })
        {
            await Reject(() => session.UploadAsync(path, bytes), "MICROPYTHON_PATH", "拒绝越界脚本路径 " + path);
        }
        transport.CorruptTemporaryHash = true;
        await Reject(() => session.UploadAsync("main.py", "different"u8.ToArray()), "MICROPYTHON_VERIFY", "传输校验失败不能替换原脚本");
        Check(File.ReadAllBytes(Path.Combine(deviceRoot, "main.py")).SequenceEqual(bytes) && !session.IsConnected && transport.Closed, "校验失败后保留原件并释放端口");
        await session.ConnectAsync(destination, "COM77");
        transport!.CorruptNextAck = true;
        await Reject(() => session.ExecuteAsync("print(1)"), "MICROPYTHON_PROTOCOL", "异常确认字节不能被视为成功");
        Check(!session.IsConnected && transport.Closed, "协议错位释放连接");
        await session.ConnectAsync(destination, "COM77");
        transport!.HangNext = true;
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try
            {
                await session.ExecuteAsync("print(1)", cancel.Token);
                throw new InvalidOperationException("未取消");
            }
            catch (OperationCanceledException) { Check(!session.IsConnected && transport.Closed, "取消等待并释放端口"); }
        }
        await using var wrong = new MicroPythonSessionService(hub, _ => new FixtureTransport(python, deviceRoot, selected) { WrongIdentity = true });
        await Reject(() => wrong.ConnectAsync(destination, "COM77"), "MICROPYTHON_IDENTITY", "拒绝解释器板型不匹配");
    }
}
await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results);
Console.WriteLine($"PASS {results.Count}; protocol/filesystem simulation only, no hardware accessed.");
return 0;

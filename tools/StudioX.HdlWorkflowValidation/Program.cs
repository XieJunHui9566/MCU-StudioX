using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

var repository = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var original = Path.GetFullPath(args[2]);
var root = Path.Combine(output, "含 空格", "fixture");
if (args.Length == 4 && args[3] == "--hardware-only")
{
    await WorkflowHardwareChecks.RunAsync(Path.Combine(repository, "artifacts/tool-runtime"), root, output);
    return;
}
Directory.CreateDirectory(root);
foreach (var file in Directory.EnumerateFiles(Path.Combine(original, "device"), "*", SearchOption.AllDirectories)
    .Concat(new[] { "CMakeLists.txt", ".studiox/project.json", ".studiox/toolchain.lock.json", "src/main.c",
        "logic/user_logic.v", "logic/logic_unit.v", "logic/pins.ve", "logic/user.sdc" }.Select(path => Path.Combine(original, path))))
{
    var target = Path.Combine(root, Path.GetRelativePath(original, file));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(file, target, true);
}
if (args.Length == 4 && args[3] == "--gpio-electrical")
{
    // 在隔离夹具的自定义逻辑旁增加独立 GPIO，验证两类逻辑共享的电气约束流程。
    await File.AppendAllTextAsync(Path.Combine(root, "logic/pins.ve"), "\nGPIO9_0 PIN_2 #GPIO_EXT #@StudioX:GPIO pull=UP output=OPEN_DRAIN\n");
}
var catalog = new ToolsetCatalog(Path.Combine(repository, "artifacts/tool-runtime/toolsets"));
var native = new Ag32NativeBuildService(catalog);
var settings = new Ag32NativeBuildSettings(1, ["logic/user_logic.v", "logic/logic_unit.v"], ["logic"], [], ["logic/user.sdc"]);
await native.SaveSettingsAsync(root, settings);
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
async Task Reject(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (Exception error) when (error is StudioXException or OperationCanceledException)
    {
        checks.Add(message);
        Console.WriteLine("PASS " + message);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + message);
}
var build = await new BuildService(catalog).BuildAsync(root, new Progress<string>(Console.WriteLine));
await File.WriteAllTextAsync(Path.Combine(output, "joint-build.log"), build.Log);
Check(build.Success, "含中文空格路径的真实 MCU 与 FPGA 联合构建");
var image = await native.RequireImageAsync(root);
Check(image.ByteCount > 0 && image.ByteCount <= 102400 && image.FlashAddress == 0x80027000, "位流容量与逻辑地址准确");
Check(build.Artifacts.Any(path => path.EndsWith("firmware.bin")) && build.Artifacts.Contains(image.Path), "同一次构建返回两段镜像");
var sdc = Path.Combine(root, "logic/user.sdc");
var originalSdc = await File.ReadAllTextAsync(sdc);
await File.AppendAllTextAsync(sdc, "\n# changed");
await Reject(async () => await native.RequireImageAsync(root), "SDC 变化拒绝旧镜像");
await File.WriteAllTextAsync(sdc, originalSdc);
var bytes = await File.ReadAllBytesAsync(image.Path);
var corrupted = bytes.ToArray();
corrupted[0] ^= 1;
await File.WriteAllBytesAsync(image.Path, corrupted);
await Reject(async () => await native.RequireImageAsync(root), "位流被修改拒绝下载");
await File.WriteAllBytesAsync(image.Path, bytes);
await native.RequireImageAsync(root);
var toolLockPath = Path.Combine(root, ".studiox/ag32-logic-toolchain.lock.json");
var toolLockBytes = await File.ReadAllBytesAsync(toolLockPath);
await File.AppendAllTextAsync(toolLockPath, "\n");
await Reject(async () => await native.RequireImageAsync(root), "工具锁定变化拒绝旧镜像");
await File.WriteAllBytesAsync(toolLockPath, toolLockBytes);

Directory.CreateDirectory(Path.Combine(root, "sim"));
var bench = Path.Combine(root, "sim/tb_logic.v");
var testbench = """
    `timescale 1ns/1ps
    module tb_logic;
      reg a=0, b=0, sel=0, clk=0, rst=0;
      reg unknown;
      reg floating=1'bz;
      wire y, q, parity;
      integer n;
      logic_unit dut(a,b,sel,clk,rst,y,q,parity);
      initial begin
        #1 rst=1; #4;
        if(q!==0 || parity!==0) $fatal(1,"reset failed");
        rst=0;
        for(n=0;n<8;n=n+1) begin
          {sel,b,a}=n; #5;
          if(y !== (sel ? (a|b) : (a&b))) $fatal(1,"truth table failed");
          clk=1; #5;
          if(q !== y) $fatal(1,"register failed");
          clk=0; #5;
        end
        $display("ASSERTIONS_PASSED");
        $finish;
      end
    endmodule
    """;
await File.WriteAllTextAsync(bench, testbench);
var simulation = new HdlSimulationEngine(catalog);
var config = new HdlSimulationSettings(1, ["logic/logic_unit.v"], ["logic"], [], "sim/tb_logic.v", "tb_logic", 1000, 10);
await simulation.SaveSettingsAsync(root, config);
var result = await simulation.RunAsync(root, config);
Check(File.ReadAllText(result.LogPath).Contains("ASSERTIONS_PASSED"), "真实 Icarus testbench 的组合、复位及寄存器断言通过");
Check(result.Waveform.NanosecondsPerTick == .001m && result.Waveform.EndTick == 125000, "VCD 精度与事件时间准确");
Check(result.Waveform.Signals.Any(signal => signal.Changes.Any(change => change.Value == "x")) &&
    result.Waveform.Signals.Any(signal => signal.Changes.Any(change => change.Value == "z")), "未知态与高阻态保留");
Check(result.Waveform.Signals.Any(signal => signal.Name.Contains("count") && signal.Width == 2), "总线与层级信号保留");
var limited = await simulation.RunAsync(root, config with { DurationNanoseconds = 2 });
Check(limited.Warnings.Any(warning => warning.Contains("时间上限")), "达到时间上限提示断言可能未完成");
await File.WriteAllTextAsync(bench, testbench.Replace("reset failed", "intentional failure").Replace("q!==0 || parity!==0", "1"));
await Reject(async () => await simulation.RunAsync(root, config), "$fatal 拒绝伪成功");
await File.WriteAllTextAsync(bench, "module tb_logic; initial forever begin end endmodule");
await Reject(async () => await simulation.RunAsync(root, config with { TimeoutSeconds = 1 }), "无时间推进的 testbench 被超时终止");
await File.WriteAllTextAsync(bench, testbench);
Check(await simulation.IsCurrentAsync(result), "恢复相同内容后波形输入一致");
await File.AppendAllTextAsync(bench, "\n// edit");
Check(!await simulation.IsCurrentAsync(result), "源码修改标记历史波形");
await File.WriteAllTextAsync(bench, testbench);
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject(async () => await simulation.RunAsync(root, config, cancelled.Token), "取消请求不启动仿真");
}
await Reject(async () => await simulation.RunAsync(root, config with { TestbenchTop = "bad;name" }), "非法顶层标识符被拒绝");
await Reject(async () => await simulation.RunAsync(root, config with { TestbenchFile = "../external.v" }), "越界源路径被拒绝");
var invalidVcd = Path.Combine(output, "invalid.vcd");
await File.WriteAllTextAsync(invalidVcd, "$scope module t $end\n$var wire 1 ! q $end\n$enddefinitions $end\n#10\n0!\n#9\n1!");
await Reject(() => Task.FromResult(VcdReader.Read(invalidVcd)), "倒退 VCD 时间被拒绝");
await WorkflowMcpChecks.RunAsync(Path.Combine(repository, "artifacts/tool-runtime"), root, output, Check);
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = checks.Count, checks, root, image, simulation = result.VcdPath });
Console.WriteLine("Fixture: " + root);

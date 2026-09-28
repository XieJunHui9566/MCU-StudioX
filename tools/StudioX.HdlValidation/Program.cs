using System.Security.Cryptography;
using System.Xml.Linq;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

var repository = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var root = Path.Combine(output, "fixture");
Directory.CreateDirectory(Path.Combine(root, "logic"));
// 保留真实器件清单和工具锁定，供同一夹具进行桌面打开验证；不复制构建物或下载设置。
foreach (var path in Directory.EnumerateFiles(Path.Combine(args[2], "device"), "*", SearchOption.AllDirectories)
    .Concat(new[] { Path.Combine(args[2], ".studiox/toolchain.lock.json"), Path.Combine(args[2], "CMakeLists.txt") }))
{
    var target = Path.Combine(root, Path.GetRelativePath(args[2], path));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(path, target, true);
}
var manifest = await ProjectService.ReadAsync(args[2]);
manifest = manifest with { Name = "AG32_Logic_Preview", Logic = new("AGRV2KL48", "logic/user_logic.v", "logic/pins.ve"), PinMapping = null };
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), manifest);
Directory.CreateDirectory(Path.Combine(root, "src"));
await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), "/* Verilog 电路图软件验证工程，不用于下载。 */\nint main(void) { for (;;) {} }\n");
var entry = Path.Combine(root, "logic/user_logic.v");
var source = """
`include "width.vh"
module user_logic(input clk, input reset_n, input enable, input sel,
                  input [`WIDTH-1:0] a, b, output [`WIDTH-1:0] y, count,
                  output [3:0] upper, output ready);
  wire [`WIDTH-1:0] value;
  logic_unit #(.WIDTH(`WIDTH)) u_logic(.a(a), .b(b), .sel(sel), .y(value));
  reg [`WIDTH-1:0] counter;
  always @(posedge clk or negedge reset_n)
    if (!reset_n) counter <= 0;
    else if (enable) counter <= counter + 1'b1;
  assign count = counter;
  assign y = value ^ counter;
  assign upper = value[7:4];
  assign ready = `READY;
endmodule
""";
await File.WriteAllTextAsync(entry, source);
await File.WriteAllTextAsync(Path.Combine(root, "logic/width.vh"), "`define WIDTH 8\n");
await File.WriteAllTextAsync(Path.Combine(root, "logic/logic_unit.v"), """
module logic_unit #(parameter WIDTH=8)(input [WIDTH-1:0] a, b, input sel, output [WIDTH-1:0] y);
  assign y = sel ? (a & b) : (a | b);
endmodule
""");
var service = new HdlSchematicService(Path.Combine(repository, "artifacts/tool-runtime"));
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
}
async Task Reject(Func<Task> action, string message, string? contains = null)
{
    try
    {
        await action();
    }
    catch (Exception error) when (error is not InvalidOperationException)
    {
        Check(contains is null || error.ToString().Contains(contains, StringComparison.OrdinalIgnoreCase) ||
            error is StudioXException studio && studio.Code == contains, message + " 原始诊断");
        return;
    }
    throw new InvalidOperationException(message + " 未拒绝");
}
var settings = (await service.ReadSettingsAsync(root)) with { TopModule = "user_logic", Defines = ["READY=1"] };
Check(settings.Sources.Length == 2, "发现同目录多文件 Verilog");
await service.SaveSettingsAsync(root, settings);
var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(entry)));
var result = await service.GenerateAsync(root, settings);
Check(result.Modules.Length == 2 && result.TopModule == "user_logic", "真实 Yosys 保留参数化子模块");
var top = result.Modules.Single(module => module.IsTop);
Check(top.Cells.Any(cell => HdlDiagramLayout.IsSequential(cell.Type)), "计数器综合为触发器");
Check(result.Modules.SelectMany(module => module.Cells).Any(cell => cell.Type == "$and"), "与门来自真实网表");
Check(result.Modules.SelectMany(module => module.Cells).Any(cell => cell.Type == "$mux"), "条件表达式综合为 MUX");
var diagram = service.CreateDiagram(top);
Check(diagram.Wires.Any(wire => wire.BitMappings.Length == 8), "保留八位总线");
Check(diagram.Wires.Any(wire => wire.BitMappings.Length == 4 && wire.BitMappings[0].Contains("[4]")), "切片连接保留源位索引");
Check(diagram.Nodes.Any(node => node.Id == "constant:1"), "常量宏形成逻辑高电平");
Check(diagram.Wires.Any(wire => wire.Points.Length == 6), "时序反馈线没有丢失");
Check(diagram.Wires.GroupBy(wire => (wire.SourceNode, wire.SourcePort)).Any(group => group.Count() > 1), "信号扇出保留");
Check(top.Cells.All(cell => cell.Source?.Contains("logic/") == true), "网表携带工程相对源码位置");
await service.ExportSvgAsync(diagram, Path.Combine(output, "schematic.svg"));
Check(XDocument.Load(Path.Combine(output, "schematic.svg")).Root?.Name.LocalName == "svg", "SVG 是有效独立 XML 文档");
Check(await service.IsCurrentAsync(result), "结果快照与工程一致");
Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(entry))) == before, "综合未改变用户源码");
var flattened = await service.GenerateAsync(root, settings with { Flatten = true });
Check(flattened.Modules.Length == 1 && flattened.Modules[0].Cells.Length > top.Cells.Length, "可展开子模块综合");
var extraHeader = Path.Combine(root, "logic/new.vh");
await File.WriteAllTextAsync(extraHeader, "// added header\n");
Check(!await service.IsCurrentAsync(result), "新增依赖文件使旧图失效");
File.Delete(extraHeader);
await File.WriteAllTextAsync(entry, "`include \"C:/external.vh\"\n" + source);
await Reject(() => service.GenerateAsync(root, settings), "拒绝快照外部绝对 include", "HDL_DEPENDENCY");
await File.WriteAllTextAsync(entry, """
(* blackbox *) module vendor_ip(input a, output y); endmodule
module user_logic(input a, output y); vendor_ip u(.a(a), .y(y)); endmodule
""");
var blackbox = await service.GenerateAsync(root, settings);
Check(blackbox.Modules.Any(module => module.IsBlackBox), "显式黑盒保留模块标记");
Directory.CreateDirectory(Path.Combine(root, "logic/common defs"));
await File.WriteAllTextAsync(Path.Combine(root, "logic/common defs/flags.vh"), "`define FLAG 1'b1\n");
await File.WriteAllTextAsync(entry, "`include \"flags.vh\"\nmodule user_logic(output y); assign y = `FLAG; endmodule\n");
var spaced = await service.GenerateAsync(root, settings with { IncludeDirectories = ["logic/common defs"] });
Check(spaced.Modules.Single(module => module.IsTop).Ports[0].Bits.SequenceEqual(new[] { "1" }), "带空格的包含目录真实解析成功");
await File.WriteAllTextAsync(entry, "module user_logic(input a, output y); assign y = missing_signal; endmodule\n");
var warning = await service.GenerateAsync(root, settings);
Check(warning.Warnings.Length > 0, "未驱动信号的原始警告可见");
await File.WriteAllTextAsync(entry, "module user_logic(input [15:8] a, output [0:7] y); assign y = a; endmodule\n");
var reversed = await service.GenerateAsync(root, settings);
Check(service.CreateDiagram(reversed.Modules.Single(module => module.IsTop)).Wires.Single().BitMappings[0] == "a[8] → y[7]",
    "升序和非零起始总线使用实际声明的位索引");
await File.WriteAllTextAsync(Path.Combine(root, "logic/init.mem"), "12\n34\n56\n78\n");
await File.WriteAllTextAsync(entry, """
module user_logic(input [1:0] addr, output [7:0] y);
  reg [7:0] mem[0:3];
  initial $readmemh("logic/init.mem", mem);
  assign y = mem[addr];
endmodule
""");
var memory = await service.GenerateAsync(root, settings);
Check(memory.Modules.Single(module => module.IsTop).Cells.Any(cell => cell.Type.StartsWith("$mem", StringComparison.Ordinal)),
    "初始化存储器使用快照数据生成真实存储单元");
await File.WriteAllTextAsync(entry, source);
await File.AppendAllTextAsync(entry, "\n// modified\n");
Check(!await service.IsCurrentAsync(result), "源码变化使旧图失效");
await File.WriteAllTextAsync(entry, "module broken(input ; endmodule");
await Reject(() => service.GenerateAsync(root, settings), "语法错误拒绝显示旧网表", "syntax error");
await File.WriteAllTextAsync(entry, "module user_logic(input a, output y); unknown_part u(.a(a), .y(y)); endmodule");
await Reject(() => service.GenerateAsync(root, settings), "未知模块拒绝综合", "unknown_part");
await File.WriteAllTextAsync(entry, source);
await Reject(() => service.GenerateAsync(root, settings with { Sources = ["../escape.v"] }), "阻止工程外部源文件");
await Reject(() => service.GenerateAsync(root, settings with { TopModule = "top; shell bad" }), "阻止顶层脚本注入");
await Reject(() => service.GenerateAsync(root, settings with { Defines = ["READY=1; shell bad"] }), "阻止宏参数注入");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject(() => service.GenerateAsync(root, settings, cancelled.Token), "支持取消综合");
}
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), manifest with { Logic = null });
await Reject(() => service.GenerateAsync(root, settings), "基础映射工程不误用自定义逻辑入口", "HDL_PROJECT");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), manifest with { DeviceId = "STM32F407ZGT6" });
await Reject(() => service.GenerateAsync(root, settings), "非 AG32 工程不开放预览");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), manifest);
await HdlMcpChecks.RunAsync(Path.Combine(repository, "artifacts/tool-runtime"), root, output, Check);
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = checks.Count, checks, fixture = root, result.ToolVersion });
Console.WriteLine($"PASS {checks.Count} checks\n{root}");

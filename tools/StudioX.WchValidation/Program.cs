using StudioX.Application.CodeIntelligence;
using StudioX.Engine;

if (args is ["--prepare-interrupts-ram", var ramRuntime, var ramOutput])
{
    await WchRamInterruptChecks.PrepareAsync(ramRuntime, ramOutput);
    return;
}
if (args is ["--run-interrupts-ram", var runRuntime, var runOutput, var backup])
{
    await WchRamInterruptChecks.RunAsync(runRuntime, runOutput, backup);
    return;
}
if (args is ["--interrupts", var isaRuntime, var isaOutput, var architecture])
{
    await WchInterruptChecks.RunAsync(isaRuntime, isaOutput, architecture);
    return;
}
if (args is ["--interrupts", var interruptRuntime, var interruptOutput])
{
    await WchInterruptChecks.RunAsync(interruptRuntime, interruptOutput);
    return;
}
if (args is ["--download", var runtime, var archive, var output])
{
    await WchDownloadChecks.RunAsync(runtime, archive, output);
    return;
}
if (args.Length != 3) throw new ArgumentException("Usage: WchValidation <runtime> <CH32V307-project> <cache-directory>");
var project = await ProjectService.ReadAsync(args[1]);
if (project.CompilerId != "wch-gcc-12.2.0-v1.4") throw new Exception("Expected WCH project");
await using var service = new CodeIntelligenceService(Path.GetFullPath(args[0]), Path.GetFullPath(args[2]));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await service.StartAsync(args[1], timeout.Token);
const string prefix = "#include \"system_config.h\"\n";
foreach (var (partial, expected) in new[] { ("GPIO_SetB", "GPIO_SetBits"), ("RCC->", "CFGR0") })
{
    var source = prefix + "void example(void) { " + partial + "\n}";
    var completions = await service.CompleteAsync("src/main.c", source, source.LastIndexOf(partial, StringComparison.Ordinal) + partial.Length, timeout.Token);
    if (!completions.Any(c => c.InsertText.Contains(expected, StringComparison.Ordinal))) throw new Exception("Missing completion: " + expected);
    Console.WriteLine("PASS completion: " + expected);
}
var text = prefix + "void example(void) { GPIO_SetBits(GPIOA, GPIO_Pin_0); }\n";
var offset = text.IndexOf("GPIO_SetBits", StringComparison.Ordinal) + 2;
var locations = await service.NavigateAsync("src/main.c", text, offset, true, timeout.Token);
if (locations.Count == 0) throw new Exception("Missing WCH library declaration");
var hover = await service.HoverAsync("src/main.c", text, offset, timeout.Token);
if (hover is null) throw new Exception("Missing WCH library hover");
var standard = "#include <stdint.h>\nuint32_t value;\n";
var types = await service.NavigateAsync("src/main.c", standard, standard.IndexOf("uint32_t", StringComparison.Ordinal) + 2, true, timeout.Token);
if (types.Count == 0 || !(await service.ReadNavigationDocumentAsync(types[0], timeout.Token)).IsReadOnly)
    throw new Exception("Missing readonly WCH standard header navigation");
Console.WriteLine("PASS declaration, hover, readonly compiler header navigation; no hardware access");

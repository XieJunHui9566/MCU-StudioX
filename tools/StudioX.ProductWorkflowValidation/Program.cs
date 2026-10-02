using StudioX.ProductWorkflowValidation;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

if (args.Length is not (2 or 4)) { throw new ArgumentException("Usage: ProductWorkflowValidation <new-output> <sample-plugin> [toolsets-root F407-pack]"); }
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) { throw new ArgumentException("Output must be new."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
try
{
    await FaultChecks.RunAsync(root, Check);
    await DistributionChecks.RunAsync(root, Check);
    await ComponentChecks.RunAsync(root, Check);
    await PluginChecks.RunAsync(root, Path.GetFullPath(args[1]), Check);
    BuildChecks.Run(Check);
    await using var debugger = new DebugSessionService(Path.Combine(root, "debug-data"));
    var launch = new DebugLaunchService(debugger, new OpenOcdService(new ToolsetCatalog(Path.Combine(root, "empty-tools"))));
    try { await launch.StartAsync(Path.Combine(root, "missing-project"), false); Check(false, "missing preparation rejected"); }
    catch (Exception) { Check(!launch.Current.Busy && launch.Current.Diagnostic is not null && launch.Current.Steps[0].Status == "失败" && !debugger.IsActive, "local preparation failure retains stage and original diagnostic without starting target"); }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await launch.StartAsync(root, false, cancelled.Token); Check(false, "cancel preparation"); }
    catch (OperationCanceledException) { Check(!launch.Current.Busy && launch.Current.Steps[0].Status == "已取消", "cancelled preparation drains and marks cancellation"); }
    if (args.Length == 4) { await NativeChecks.RunAsync(root, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check); }
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = true, hardware = false, checks });
}
catch (Exception error)
{
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = false, hardware = false, checks, diagnostic = error.ToString() });
    throw;
}

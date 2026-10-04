using StudioX.Foundation;
using StudioX.SecurityValidation;

if (args is ["--process-fixture", var marker])
{
    await ProcessChecks.RunFixtureAsync(marker);
    return 0;
}
if (args is ["--process-child"])
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}
if (args is not [var destination])
{
    throw new ArgumentException("Usage: SecurityValidation <new-output>");
}
var root = Path.GetFullPath(destination);
if (Directory.Exists(root))
{
    throw new ArgumentException("Use a new output directory.");
}
Directory.CreateDirectory(root);
var results = new List<object>();
var success = true;
foreach (var (name, run) in new (string, Func<Task>)[]
{
    ("windows-path-aliases", () => PathChecks.RunAsync(root)),
    ("pack-path-boundaries", () => PackChecks.RunAsync(root)),
    ("process-failure-cleanup", () => ProcessChecks.RunAsync(root)),
    ("plugin-protocol-backpressure", () => ProtocolChecks.RunAsync())
})
{
    try
    {
        await run();
        results.Add(new
        {
            name,
            passed = true,
            diagnostic = (string?)null
        });
        Console.WriteLine("PASS " + name);
    }
    catch (Exception error)
    {
        success = false;
        results.Add(new
        {
            name,
            passed = false,
            diagnostic = error.ToString()
        });
        Console.Error.WriteLine("FAIL " + name + ": " + error);
    }
}
await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success, hardware = false, results });
return success ? 0 : 1;

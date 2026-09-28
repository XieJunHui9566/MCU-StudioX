using StudioX.Foundation;
using StudioX.PluginValidation;

var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var archive = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repository,
    "artifacts/validation/plugin-sdk-current/studiox.workspace-overview-1.0.0.studioxplugin");
var cli = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(repository,
    "artifacts/validation/plugin-development-build/bin/StudioX.Cli/debug_win-x64/StudioX.Cli.exe");
var scratch = Path.Combine(repository, "artifacts/validation/plugin-system-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var checks = new ValidationChecks();
SchemaChecks.Run(checks);
await RepositoryChecks.RunAsync(archive, scratch, checks);
await WorkspaceChecks.RunAsync(repository, archive, cli, scratch, checks);
await LifecycleChecks.RunAsync(repository, scratch, checks);
await JsonStore.WriteAsync(Path.Combine(scratch, "results.json"), new { success = true, checks = checks.Results });
Console.WriteLine("Evidence: " + scratch);

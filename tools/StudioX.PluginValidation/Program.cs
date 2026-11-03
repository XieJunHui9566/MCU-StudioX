using StudioX.Foundation;
using StudioX.PluginValidation;

var explicitPaths = args is ["--verify", _, _, _, _, _, _];
if (args.Length > 2 && !explicitPaths) { throw new ArgumentException("Usage: --verify <repository> <archive> <cli> <plugin-host-directory> <runtime-fixture-directory> <new-output>"); }
var repository = explicitPaths ? Path.GetFullPath(args[1]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var archive = explicitPaths ? Path.GetFullPath(args[2]) : args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repository,
    "artifacts/validation/plugin-sdk-current/studiox.workspace-overview-1.0.0.studioxplugin");
var cli = explicitPaths ? Path.GetFullPath(args[3]) : args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(repository,
    "artifacts/validation/plugin-development-build/bin/StudioX.Cli/debug_win-x64/StudioX.Cli.exe");
var scratch = explicitPaths ? Path.GetFullPath(args[6]) : Path.Combine(repository, "artifacts/validation/plugin-system-" + Guid.NewGuid().ToString("N"));
if (Directory.Exists(scratch) || File.Exists(scratch)) { throw new IOException("Use a new validation output directory."); }
Directory.CreateDirectory(scratch);
var checks = new ValidationChecks();
SchemaChecks.Run(checks);
await RepositoryChecks.RunAsync(archive, scratch, checks);
await WorkspaceChecks.RunAsync(repository, archive, cli, scratch, checks, explicitPaths ? Path.GetFullPath(args[4]) : null);
await LifecycleChecks.RunAsync(repository, scratch, checks, explicitPaths ? Path.GetFullPath(args[5]) : null);
await JsonStore.WriteAsync(Path.Combine(scratch, "results.json"), new { success = true, checks = checks.Results });
Console.WriteLine("Evidence: " + scratch);

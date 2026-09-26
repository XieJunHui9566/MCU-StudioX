using StudioX.Packages;

if (args.Length == 2 && args[0] == "--prune")
{
    if (!Path.IsPathFullyQualified(args[1])) throw new ArgumentException("--prune requires an absolute pack root.");
    var root = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!Directory.Exists(root) || string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("--prune requires an existing pack directory, not a drive root.");
    Console.WriteLine("Pack root: " + root);
    var result = await new PackRepository(root).PruneSupersededAsync();
    foreach (var removed in result.Removed)
        Console.WriteLine($"REMOVED {removed.Id}/{removed.Version} -> {removed.ReplacementVersion}; {removed.Bytes} bytes");
    foreach (var failure in result.Failures)
        Console.WriteLine($"FAILED {failure.Id}/{failure.Version}: {failure.Message}");
    Console.WriteLine($"Removed={result.Removed.Count}; Failures={result.Failures.Count}; ReclaimedBytes={result.ReclaimedBytes}");
    Environment.ExitCode = result.Failures.Count == 0 ? 0 : 1;
    return;
}

if (args.Length != 1) throw new ArgumentException("Usage: StudioX.PackRetentionValidation <new-output-directory> | --prune <absolute-pack-root>");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Use a new output directory.");
Directory.CreateDirectory(output);
await new RetentionChecks(output).RunAsync();

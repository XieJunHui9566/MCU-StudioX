using StudioX.EspressifValidation;

if (args is ["--portability-finish", var movedFixture, var movedEvidence])
{
    await PortabilityChecks.FinishAsync(Path.GetFullPath(movedFixture), Path.GetFullPath(movedEvidence));
    return 0;
}

if (args is ["--environment-reload", var reloadRuntime, var reloadProject, var reloadOutput])
{
    var evidence = Path.GetFullPath(reloadOutput);
    if (Directory.Exists(evidence))
    {
        throw new IOException("Use a new reload evidence directory.");
    }
    Directory.CreateDirectory(evidence);
    var checks = new List<string>();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await EnvironmentReloadChecks.RunAsync(Path.GetFullPath(reloadRuntime), Path.GetFullPath(reloadProject), evidence,
        (condition, label) => { if (!condition) { throw new InvalidOperationException(label); } checks.Add(label); Console.WriteLine("PASS " + label); }, cancellation.Token);
    await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), System.Text.Json.JsonSerializer.Serialize(new
    {
        passed = true,
        checks,
        hardware = false
    }));
    return 0;
}

if (args is ["--portability", var portabilityRuntime, var portabilityEspPack, var portabilityArmPack, var portabilityOutput])
{
    await PortabilityChecks.RunAsync(Path.GetFullPath(portabilityRuntime), Path.GetFullPath(portabilityEspPack), Path.GetFullPath(portabilityArmPack), Path.GetFullPath(portabilityOutput));
    return 0;
}
if (args is ["--portability-resume", var preparedRuntime, var preparedEspPack, var preparedArmPack, var preparedOutput])
{
    await PortabilityChecks.RunAsync(Path.GetFullPath(preparedRuntime), Path.GetFullPath(preparedEspPack), Path.GetFullPath(preparedArmPack), Path.GetFullPath(preparedOutput), usePreparedFixture: true);
    return 0;
}

if (args is ["--language-diagnostics", var diagnosticsRuntime, var diagnosticsProjects, var diagnosticsOutput])
{
    await EspressifLanguageDiagnosticChecks.RunAsync(Path.GetFullPath(diagnosticsRuntime), Path.GetFullPath(diagnosticsProjects), Path.GetFullPath(diagnosticsOutput));
    return 0;
}

if (args is ["--bundle-upgrade", var previousPacks, var currentPacks, var upgradeOutput])
{
    await EspressifBundleUpgradeChecks.RunAsync(Path.GetFullPath(previousPacks), Path.GetFullPath(currentPacks), Path.GetFullPath(upgradeOutput));
    return 0;
}
if (args is ["--official", var officialPacks, var officialOutput])
{
    await EspressifProjectChecks.RunAsync(Path.GetFullPath(officialPacks), Path.GetFullPath(officialOutput), officialOnly: true);
    return 0;
}
if (args is ["--language-alias", var aliasRuntime, var aliasProject, var aliasOutput])
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    await EspressifLanguageAliasChecks.RunAsync(Path.GetFullPath(aliasRuntime), Path.GetFullPath(aliasProject), Path.GetFullPath(aliasOutput), cancellation.Token);
    return 0;
}
if (args is ["--language-fallback", var fallbackRuntime, var fallbackProject, var fallbackOutput])
{
    await EspressifLanguageChecks.RunFallbackAsync(Path.GetFullPath(fallbackRuntime), Path.GetFullPath(fallbackProject), Path.GetFullPath(fallbackOutput));
    return 0;
}
if (args is ["--language", var runtime, var projects, var output])
{
    await EspressifLanguageChecks.RunAsync(Path.GetFullPath(runtime), Path.GetFullPath(projects), Path.GetFullPath(output));
    return 0;
}
if (args is not [var packsDirectory, var outputDirectory])
{
    Console.Error.WriteLine("Usage: StudioX.EspressifValidation <packs-directory> <new-output-directory>");
    return 2;
}
await EspressifProjectChecks.RunAsync(Path.GetFullPath(packsDirectory), Path.GetFullPath(outputDirectory));
return 0;

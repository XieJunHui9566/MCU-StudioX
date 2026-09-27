using StudioX.EspressifValidation;

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

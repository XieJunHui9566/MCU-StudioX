using StudioX.EnvironmentReliabilityValidation;

if (args is ["--repair-child", var root, var archive, var checkpoint])
{
    await RecoveryChecks.ChildAsync(root, archive, checkpoint);
    return;
}
if (args is ["--download-child", var downloadRoot, var source, var length, var hash])
{
    await RecoveryChecks.DownloadChildAsync(downloadRoot, source, long.Parse(length), hash);
    return;
}
if (args is ["--boundaries", var output, var ninja])
{
    await RecoveryChecks.RunAsync(Path.GetFullPath(output), Path.GetFullPath(ninja));
    return;
}
if (args is ["--offline", var plan, var offlineOutput])
{
    await OfflineChecks.RunAsync(Path.GetFullPath(plan), Path.GetFullPath(offlineOutput));
    return;
}
if (args is ["--offline-resume", var resumePlan, var resumeOutput])
{
    await OfflineChecks.RunAsync(Path.GetFullPath(resumePlan), Path.GetFullPath(resumeOutput), resume: true);
    return;
}
throw new ArgumentException("Use --boundaries <new-output> <existing-ninja>, --offline <explicit-plan.json> <new-output>, or --offline-resume <same-plan.json> <existing-evidence>.");

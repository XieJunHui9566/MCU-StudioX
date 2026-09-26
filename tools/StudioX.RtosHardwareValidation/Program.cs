using StudioX.RtosHardwareValidation;

if (args is ["--self-test"])
{
    AcceptanceChecks.Run();
    return;
}

if (args is ["--download", var project, var runtime, var output, var expectedBinSha256])
{
    Environment.ExitCode = await new DownloadCheck(project, runtime, output, expectedBinSha256).RunAsync();
    return;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage: --prepare|--attach <project> <runtime> <output> [comma-separated-object-symbols]");
    Console.WriteLine("--prepare only validates local configuration and existing artifacts; --attach supports STM32F407ZG / ST-Link or CH32V307VCT6, RCT6, WCU6 / WCH-Link.");
    Console.WriteLine("--self-test validates acceptance rules with local fixtures only; it never connects hardware.");
    Console.WriteLine("--download <project> <runtime> <output> <expected-BIN-sha256> writes only the explicitly authorized 7984-byte CH32V307VCT6 / WCH-Link test firmware; it never builds or attaches.");
    return;
}
if (args.Length is < 4 or > 5 || args[0] is not ("--prepare" or "--attach"))
    throw new ArgumentException("Expected --prepare|--attach <project> <runtime> <output> [object-symbols].");

var run = new HardwareCheck(args[1], args[2], args[3], args.Length == 5 ? args[4] : null);
Environment.ExitCode = await run.RunAsync(args[0] == "--attach");

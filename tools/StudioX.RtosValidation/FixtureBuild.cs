using System.Diagnostics;

internal static class FixtureBuild
{
    public static async Task<string> BuildAsync(string gcc, string fixture, string output, bool arm, bool minimal)
    {
        Directory.CreateDirectory(output);
        var elf = Path.Combine(output, minimal ? "freertos-minimal.elf" : "freertos-snapshot.elf");
        var args = new List<string>
        {
            "-g3", "-O0", "-fno-eliminate-unused-debug-types", "-fno-common", "-nostdlib",
            "-Wl,-Ttext=0x08000000", "-Wl,-Tdata=0x20000000", "-Wl,--build-id=none",
            "-o", elf, fixture
        };
        args.AddRange(arm ? ["-mcpu=cortex-m4", "-mthumb"] : ["-march=rv32imac", "-mabi=ilp32"]);
        if (minimal) args.Add("-DFIXTURE_MINIMAL=1");
        var start = new ProcessStartInfo(gcc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("GCC failed to start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var log = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.ChangeExtension(elf, ".build.log"), log);
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture GCC failed: " + log);
        return elf;
    }
}

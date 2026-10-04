using StudioX.Foundation;

internal static class AnalogRegisterChecks
{
    internal static async Task RunAsync(string project, string gcc)
    {
        var root = Path.Combine(project, "analog-register-checks");
        Directory.CreateDirectory(Path.Combine(root, "vendor"));
        foreach (var file in new[] { "StudioX_System.h", "StudioX_System.c", "StudioX_Board.h", "vendor/analog_ip.h" })
        {
            File.Copy(Path.Combine(project, "device/studiox", file), Path.Combine(root, file));
        }
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "native")))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }
        var runner = new ProcessRunner();
        var compiled = await runner.RunAsync(new(gcc, ["-std=gnu17", "-O2", "-I.", "StudioX_System.c", "analog_checks.c", "-o", "analog_checks.exe"], root, TimeSpan.FromSeconds(30)));
        File.WriteAllText(Path.Combine(root, "compile.log"), compiled.StandardOutput + compiled.StandardError);
        if (!compiled.Success)
        {
            throw new InvalidOperationException("Analog host compile: " + root);
        }
        var result = await runner.RunAsync(new(Path.Combine(root, "analog_checks.exe"), [], root, TimeSpan.FromSeconds(15)));
        File.WriteAllText(Path.Combine(root, "result.txt"), result.StandardOutput + result.StandardError);
        if (!result.Success)
        {
            throw new InvalidOperationException("Analog register fixture: " + root);
        }
        // 同一套寄存器检查再覆盖没有独立 BUSCLK 的系统时钟直连模式。
        var header = Path.Combine(root, "StudioX_System.h");
        File.WriteAllText(header, File.ReadAllText(header).Replace("STUDIOX_ANALOG_SEPARATE_BUS 1", "STUDIOX_ANALOG_SEPARATE_BUS 0", StringComparison.Ordinal));
        compiled = await runner.RunAsync(new(gcc, ["-std=gnu17", "-O2", "-I.", "StudioX_System.c", "analog_checks.c", "-o", "analog_shared_checks.exe"], root, TimeSpan.FromSeconds(30)));
        File.WriteAllText(Path.Combine(root, "shared-compile.log"), compiled.StandardOutput + compiled.StandardError);
        if (!compiled.Success)
        {
            throw new InvalidOperationException("Shared clock host compile: " + root);
        }
        result = await runner.RunAsync(new(Path.Combine(root, "analog_shared_checks.exe"), [], root, TimeSpan.FromSeconds(15)));
        File.WriteAllText(Path.Combine(root, "shared-result.txt"), result.StandardOutput + result.StandardError);
        if (!result.Success)
        {
            throw new InvalidOperationException("Shared clock register fixture: " + root);
        }
    }
}

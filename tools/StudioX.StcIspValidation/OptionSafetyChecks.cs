using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>执行构建内嵌的门禁脚本，避免测试源码脚本却漏掉实际分发资源。</summary>
internal static class OptionSafetyChecks
{
    internal static async Task RunAsync(string python, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new validation directory.");
        }
        Directory.CreateDirectory(output);
        var script = Path.Combine(output, "studiox-stcgal-guard.py");
        await using (var resource = typeof(StcIspService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-stcgal-guard.py")
            ?? throw new InvalidOperationException("Embedded ISP guard is missing."))
        {
            await using var file = File.Create(script);
            await resource.CopyToAsync(file);
        }
        var resultPath = Path.Combine(output, "options.json");
        var result = await new ProcessRunner().RunAsync(new(Path.GetFullPath(python),
            ["-B", Path.GetFullPath("tools/StudioX.StcIspValidation/option_safety_offline.py"), "--guard", script, "--output", resultPath],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(30), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"]));
        await File.WriteAllTextAsync(Path.Combine(output, "process.log"), result.StandardOutput + result.StandardError);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.StandardOutput + result.StandardError);
        }
        var denied = 0;
        foreach (var name in new[] { "STC12H1K16", "STC15H2K64S4", "IAP12H1K16", "IAP15H2K64", "IRC12H1K16", "IRC15H2K64" })
        {
            var device = new DeviceDefinition(name, name, "mcs51", 0, 4096, 0, 1024,
                "stc.sdcc", "4.5.0", "sdcc-4.5.0", [], [], [], [], "", [], [], []);
            try
            {
                StcIspCapabilities.For(device);
                throw new InvalidOperationException("Unqualified new family inherited legacy download capabilities: " + name);
            }
            catch (StudioXException error) when (error.Code == "STC_ISP_DEVICE")
            {
                denied++;
            }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true, hardware = false, newFamilyCapabilityRejections = denied,
            guardSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(script))),
            loadedEngine = typeof(StcIspService).Assembly.Location
        });
        Console.WriteLine(result.StandardOutput.Trim());
        Console.WriteLine($"PASS {denied} early capability rejections; embedded guard executed without hardware access.");
    }
}

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>验证持久化和跨系列选项契约，再用实际后端编码器交叉核对，禁止访问串口。</summary>
internal static class HardwareSettingsChecks
{
    internal static async Task RunAsync(string python, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) { throw new IOException("Use a new validation directory."); }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) { throw new InvalidOperationException(name); }
            checks.Add(name);
        }
        var targets = new[] { "STC89C52RC", "STC12C2052AD", "STC12C5A60S2", "STC15F104E", "IAP15L2K61S2", "STC8H8K64U" };
        foreach (var target in targets)
        {
            foreach (var field in StcHardwareCatalog.For(target))
            {
                foreach (var choice in field.Values)
                {
                    var options = StcHardwareOptions.FromValues(new Dictionary<string, string> { [field.Id] = choice.Value });
                    options.Validate(target, true, StcDownloadMode.Serial);
                    var settings = new StcIspSettings(Port: "COM17", ModifyHardware: true, Hardware: options);
                    var restored = JsonSerializer.Deserialize<StcIspSettings>(JsonSerializer.Serialize(settings));
                    Check(settings == restored, target + ": round trip " + field.Id + "=" + choice.Value);
                }
            }
        }
        Check(StcHardwareCatalog.For("STC32G12K128").Count == 0 && StcHardwareCatalog.For("AI8051U-34K64").Count == 0,
            "MCS-251 never inherits an 8-bit option layout");
        Check(StcHardwareCatalog.For("STC15H2K64S4").Count == 0, "new STC15H family remains explicitly unqualified");
        foreach (var options in new[] { new StcHardwareOptions { Uart2PinMode = "wrong" }, new StcHardwareOptions { LowVoltageThreshold = "8" },
            new StcHardwareOptions { WatchdogPowerOn = "yes" }, new StcHardwareOptions { Uart1Remap = "true" } })
        {
            try { options.Validate("IAP15L2K61S2", true, StcDownloadMode.Serial); throw new InvalidOperationException("invalid settings accepted"); }
            catch (StudioXException error) when (error.Code == "STC_HARDWARE_OPTION") { checks.Add("invalid value rejected"); }
        }
        var usbDraft = new StcHardwareOptions { WatchdogPowerOn = "true" };
        usbDraft.Validate("STC8H8K64U", false, StcDownloadMode.NativeUsb);
        usbDraft.Validate("STC8H8K64U", true, StcDownloadMode.NativeUsb);
        checks.Add("qualified F784 USB watchdog setting accepted");
        var unsupportedUsbDraft = new StcHardwareOptions { Uart1Remap = "true" };
        unsupportedUsbDraft.Validate("STC8H8K64U", false, StcDownloadMode.NativeUsb);
        try { unsupportedUsbDraft.Validate("STC8H8K64U", true, StcDownloadMode.NativeUsb); throw new InvalidOperationException("unqualified USB bits accepted"); }
        catch (StudioXException error) when (error.Code == "STC_USB_OPTIONS") { checks.Add("USB keeps inactive drafts, refuses applying bits"); }
        try { StcHardwareOptions.FromValues(new Dictionary<string, string> { ["unknown"] = "true" }); throw new InvalidOperationException("unknown field accepted"); }
        catch (JsonException) { checks.Add("unknown JSON properties rejected"); }
        var path = Path.Combine(output, "project");
        Directory.CreateDirectory(path);
        var saved = new StcIspSettings(Port: "COM17", ModifyHardware: false, Hardware: usbDraft);
        await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath), saved);
        Check(await StcIspSettings.ReadAsync(path) == saved, "inactive hardware draft persists across project reload");
        var contract = Path.Combine(output, "contract.json");
        await JsonStore.WriteAsync(contract, targets.Select(target => new { target, profile = StcHardwareCatalog.Profile(target), fields = StcHardwareCatalog.For(target) }));
        var guard = Path.Combine(output, "guard.py");
        await using (var source = typeof(StcIspService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-stcgal-guard.py")!)
        {
            await using var file = File.Create(guard);
            await source.CopyToAsync(file);
        }
        var run = await new ProcessRunner().RunAsync(new(Path.GetFullPath(python), ["-B", Path.GetFullPath("tools/StudioX.StcIspValidation/hardware_settings_offline.py"), guard, contract, Path.Combine(output, "backend.json")],
            Environment.CurrentDirectory, TimeSpan.FromSeconds(30), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"]));
        await File.WriteAllTextAsync(Path.Combine(output, "backend.log"), run.StandardOutput + run.StandardError);
        Check(run.Success, "actual stcgal encoders and guarded download entry: " + run.StandardError);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = true, hardware = false, checks });
        Console.WriteLine($"PASS {checks.Count} hardware settings checks; " + run.StandardOutput.Trim());
    }
}

namespace StudioX.PicValidation;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Pic;
using StudioX.Foundation;

/// <summary>普通下载的隔离 IPECMD 替身；不枚举真实 USB，验证快路径及写入前拒绝边界。</summary>
internal static class DirectDownloadChecks
{
    internal static async Task FixtureAsync(string[] arguments)
    {
        var home = arguments.Single(argument => argument.StartsWith("-Duser.home=", StringComparison.Ordinal))[12..];
        Directory.CreateDirectory(home);
        await File.AppendAllTextAsync(Path.Combine(home, "calls.jsonl"), JsonSerializer.Serialize(arguments) + "\n");
        var modePath = Path.Combine(home, "mode.txt");
        var mode = File.Exists(modePath) ? await File.ReadAllTextAsync(modePath) : "success";
        if (arguments.Contains("-T"))
        {
            Console.WriteLine("Available Tool List\n-------------------\n1  PICkit3 S.No : FIXTURE123\n2  PICkit4 S.No : NOTPK3");
            Environment.ExitCode = 50;
            return;
        }
        if (arguments.Any(argument => argument.StartsWith("-GF", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Fixture forbids full firmware readback or backup during ordinary download.");
            Environment.ExitCode = 91;
            return;
        }
        if (!arguments.Contains("-P16F877A") || !arguments.Contains("-TPPK3") || !arguments.Contains("-TSFIXTURE123"))
        {
            Console.Error.WriteLine("Selected programmer not found or target selection differs.");
            Environment.ExitCode = 3;
            return;
        }
        if (arguments.Contains("-GC"))
        {
            if (mode == "preflight-fail")
            {
                Console.Error.WriteLine("Read failed: simulated transport failure");
                Environment.ExitCode = 3;
                return;
            }
            if (mode != "missing-serial")
            {
                Console.WriteLine("Selected Programming Tool Sno:\n" + (mode == "wrong-serial" ? "OTHER123" : "FIXTURE123"));
            }
            if (mode != "missing-device")
            {
                Console.WriteLine("Target device " + (mode == "wrong-device" ? "PIC16F876A" : "PIC16F877A") + " found.");
            }
            if (mode == "mixed-device")
            {
                Console.WriteLine("Target device PIC16F876A found.");
            }
            Console.WriteLine("Device Revision ID = 8");
            if (mode == "preflight-cancel")
            {
                Console.WriteLine("FIXTURE_PREFLIGHT_WAITING");
                await Console.Out.FlushAsync();
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
            if (mode != "missing-config")
            {
                var config = mode == "protected-code" ? "1FFF" : mode == "protected-eeprom" ? "3EFF" : "3F32";
                Console.WriteLine("Configuration Memory");
                Console.WriteLine("2007  " + (mode == "garbled-config" ? "FFFFF" : config));
                if (mode == "duplicate-config")
                {
                    Console.WriteLine("2007  1FFF");
                }
                if (mode == "mixed-malformed-config")
                {
                    Console.WriteLine("2007  FFFFF");
                }
            }
            Console.WriteLine("FIXTURE_CONFIG_READY");
            await Console.Out.FlushAsync();
            await Task.Delay(100);
            Console.WriteLine(mode == "preflight-fails-zero" ? "Read failed.\nOperation Failed" :
                "Read successfully.\nOperation Succeeded");
            return;
        }
        if (arguments.Contains("-M"))
        {
            if (arguments.Contains("-Y"))
            {
                Console.Error.WriteLine("Fixture forbids a redundant standalone verify after -M.");
                Environment.ExitCode = 92;
                return;
            }
            Console.WriteLine("Selected Programming Tool Sno:\n" + (mode == "program-wrong-serial" ? "OTHER123" : "FIXTURE123"));
            Console.WriteLine("Target device " + (mode == "program-wrong-device" ? "PIC16F876A" : "PIC16F877A") + " found.");
            if (mode == "cancel")
            {
                await File.WriteAllTextAsync(Path.Combine(home, "program.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine("FIXTURE_PROGRAMMING_WAITING");
                await Console.Out.FlushAsync();
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
            if (mode == "error")
            {
                Console.Error.WriteLine("Verify failed: simulated mismatch");
                Environment.ExitCode = 7;
                return;
            }
            Console.WriteLine("FIXTURE_PROGRAM_READY");
            await Console.Out.FlushAsync();
            await Task.Delay(100);
            Console.WriteLine(mode == "no-verify" ? "Programming complete\nOperation Succeeded" :
                mode == "verify-fails-zero" ? "Programming/Verify complete\nVerification failed.\nOperation Failed" :
                "Programming/Verify complete\nProgram Succeeded.\nOperation Succeeded");
            return;
        }
        throw new InvalidOperationException("Unexpected direct-download fixture invocation");
    }

    internal static async Task RunAsync(string evidence, string runtime, string archive, bool enumerateHardware = false)
    {
        if (Directory.Exists(evidence))
        {
            throw new ArgumentException("Use new evidence directory");
        }
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var cases = new List<object>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        async Task Reject(string label, string[] codes, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (StudioXException error) when (codes.Contains(error.Code, StringComparer.Ordinal))
            {
                Check(true, label + " [" + error.Code + "]");
                return;
            }
            throw new InvalidOperationException("Expected rejection: " + label);
        }
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var pack = await services.Packs.ImportAsync(archive);
        var root = Path.Combine(evidence, "中文 PIC project");
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-gpio-4mhz", "PicDirectDownloadTest", root);
        var build = await services.Builds.BuildAsync(root);
        await File.WriteAllTextAsync(Path.Combine(evidence, "build.log"), build.Log);
        Check(build.Success, "real offline CMake / Ninja / XC8 image builds without target access");
        var firmware = await services.Builds.Pic.PreviewAsync(root);
        var originalHex = await File.ReadAllBytesAsync(firmware.HexPath);
        var image = PicHexImage.Validate(originalHex);
        Check(image.ProgramWords > 0 && (image.ConfigurationWord & 0x2100) == 0x2100,
            "actual XC8 image has reset vector, valid 14-bit words and explicit unprotected configuration");
        foreach (var invalid in new[] { originalHex[..^12], Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(originalHex).Replace(':', ';')),
            Encoding.ASCII.GetBytes(":020000000000FE\n:02400E00FF3F72\n:024200000000BC\n:00000001FF\n") })
        {
            await Reject("malformed or EEPROM-writing image", ["PIC_DOWNLOAD_IMAGE"], () => Task.Run(() => PicHexImage.Validate(invalid)));
        }
        await Reject("unsafe serial settings", ["PIC_DOWNLOAD_SETTINGS"], () => Task.Run(() => new PicDownloadSettings(ProgrammerSerial: "../other").Validate(false)));
        foreach (var configuration in new[] { ":02400E00FF1F92", ":02400E00FF3E73" })
        {
            await Reject("new image cannot enable code / EEPROM protection", ["PIC_CODE_PROTECTED"], () =>
                Task.Run(() => PicHexImage.Validate(Encoding.ASCII.GetBytes(":020000000000FE\n" + configuration + "\n:00000001FF\n"))));
        }

        var component = Path.Combine(evidence, "fixture-tools", "microchip.ipe", "6.20.0");
        var bin = Path.Combine(component, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }
        await File.WriteAllTextAsync(Path.Combine(component, "direct-fixture.jar"), "fixture marker; no native USB library is invoked");
        var hashes = Directory.GetFiles(component, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(component, path).Replace('\\', '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await JsonStore.WriteAsync(Path.Combine(component, "toolset.json"), new ToolsetManifest(1, "microchip.ipe", "6.20.0", "win-x64", "ipecmd-6.20",
            new() { ["java"] = "bin/StudioX.PicValidation.exe", ["ipecmd"] = "direct-fixture.jar" }, hashes, "PICkit3 isolated IPECMD fixture",
            new() { ["ipe"] = "6.20" }, new() { ["ipe"] = "bin" }, "pic-ipe"));
        var fixtureData = Path.Combine(evidence, "fixture-data");
        var home = Path.Combine(fixtureData, "pic/ipe-user");
        Directory.CreateDirectory(home);
        var callsPath = Path.Combine(home, "calls.jsonl");
        var modePath = Path.Combine(home, "mode.txt");
        var service = new PicDownloadService(new ToolsetCatalog(Path.Combine(evidence, "fixture-tools")), services.Builds.Pic, fixtureData);
        var listed = await service.ListAsync();
        Check(listed is [{ Serial: "FIXTURE123" }], "explicit standalone enumeration excludes other PICkit types");
        await service.SaveSettingsAsync(root, "FIXTURE123", true);

        async Task Begin(string mode)
        {
            await File.WriteAllTextAsync(modePath, mode);
            await File.WriteAllTextAsync(callsPath, "");
        }
        async Task<string[][]> Calls() => (await File.ReadAllLinesAsync(callsPath))
            .Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
        async Task Record(string name, long elapsed, int expectedPrograms)
        {
            var calls = await Calls();
            Check(calls.Count(arguments => arguments.Contains("-M")) == expectedPrograms, name + " starts the expected number of programming operations");
            Check(calls.All(arguments => !arguments.Any(argument => argument.StartsWith("-GF", StringComparison.Ordinal)) &&
                !arguments.Contains("-T") && !arguments.Contains("-Y") && !arguments.Contains("-E") &&
                !arguments.Any(argument => argument.StartsWith("-W", StringComparison.Ordinal))),
                name + " performs no full readback, re-enumeration, redundant verify, bulk erase or target power override");
            Check(!Directory.GetFiles(Path.Combine(root, ".build/pic-download"), "before.hex", SearchOption.AllDirectories).Any(),
                name + " creates no original-firmware backup or implicit restoration image");
            cases.Add(new { name, elapsedMilliseconds = elapsed, processCount = calls.Length,
                programmingCount = expectedPrograms, commands = calls });
        }

        await Begin("success");
        var preview = await service.PreviewAsync(root);
        Check((await Calls()).Length == 0, "preview validates artifacts without opening any programmer process");
        var watch = Stopwatch.StartNew();
        var success = await service.DownloadApprovedAsync(root, preview.ApprovalId);
        watch.Stop();
        var successCalls = await Calls();
        Check(success.Success && success.BackupPath is null && File.Exists(success.LogPath),
            "normal direct download reports success and a log without claiming a backup");
        Check(successCalls.Length == 2 && successCalls[0].Contains("-I") && successCalls[0].Contains("-GC") &&
            successCalls[0].Contains("-OL") && successCalls[1].Contains("-M"),
            "normal download starts exactly two IPECMD processes: narrow preflight then program with built-in verify");
        Check(successCalls.All(arguments => arguments.Contains("-TSFIXTURE123") && arguments.Contains("-TPPK3") && arguments.Contains("-P16F877A")) &&
            successCalls[1].Contains("-Z") && successCalls[1].Contains("-OID") && successCalls[1].Contains("-OL"),
            "both operations select exact chip and programmer; programming preserves EEPROM / IDs and releases target");
        using (var measured = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, ".build/pic-download/latest/result.json"))))
        {
            var timing = measured.RootElement;
            Check(timing.GetProperty("backendInvocations").GetInt32() == 2 &&
                timing.GetProperty("backupPath").ValueKind == JsonValueKind.Null &&
                timing.GetProperty("identifyMilliseconds").GetDouble() > 0 && timing.GetProperty("programMilliseconds").GetDouble() > 0 &&
                timing.GetProperty("totalMilliseconds").GetDouble() >=
                    timing.GetProperty("identifyMilliseconds").GetDouble() + timing.GetProperty("programMilliseconds").GetDouble(),
                "download result records both real child stages and total duration without a backup path");
        }
        await Record("success", watch.ElapsedMilliseconds, 1);
        await service.SaveSettingsAsync(root, "FIXTURE123", false);
        var heldPreview = await service.PreviewAsync(root);
        await Begin("success");
        Check((await service.DownloadApprovedAsync(root, heldPreview.ApprovalId)).Success, "target release remains an explicit saved programming setting");
        var heldCalls = await Calls();
        Check(heldCalls.Length == 2 && heldCalls[0].Contains("-OL") && !heldCalls[1].Contains("-OL"),
            "read-only preflight releases its connection while programming obeys ReleaseAfterProgramming=false");
        await Record("hold-target-after-programming", 0, 1);
        await service.SaveSettingsAsync(root, "FIXTURE123", true);

        foreach (var (mode, codes) in new[]
        {
            ("wrong-device", new[] { "PIC_TARGET_ID" }), ("mixed-device", new[] { "PIC_TARGET_ID" }),
            ("missing-device", new[] { "PIC_TARGET_ID" }), ("preflight-fail", new[] { "PIC_IDENTIFY" }),
            ("preflight-fails-zero", new[] { "PIC_IDENTIFY" }),
            ("wrong-serial", new[] { "PIC_PROGRAMMER_ID" }), ("missing-serial", new[] { "PIC_PROGRAMMER_ID" }),
            ("protected-code", new[] { "PIC_CODE_PROTECTED" }), ("protected-eeprom", new[] { "PIC_CODE_PROTECTED" }),
            ("missing-config", new[] { "PIC_TARGET_CONFIG" }), ("garbled-config", new[] { "PIC_TARGET_CONFIG" }),
            ("mixed-malformed-config", new[] { "PIC_TARGET_CONFIG" }),
            ("duplicate-config", new[] { "PIC_TARGET_CONFIG", "PIC_CODE_PROTECTED" })
        })
        {
            await Begin(mode);
            await Reject(mode, codes, () => service.DownloadApprovedAsync(root, preview.ApprovalId));
            Check((await Calls()).Length == 1, mode + " stops after the only preflight process");
            await Record(mode, 0, 0);
        }

        var receiptPath = Path.Combine(root, ".studiox/pic-build.json");
        var originalReceipt = await File.ReadAllBytesAsync(receiptPath);
        await Begin("success");
        await File.AppendAllTextAsync(receiptPath, "\n");
        await Reject("changed build receipt revokes old download snapshot", ["PIC_DOWNLOAD_CHANGED"],
            () => service.DownloadApprovedAsync(root, preview.ApprovalId));
        Check((await Calls()).Length == 0, "changed build receipt is rejected before any IPECMD process");
        await File.WriteAllBytesAsync(receiptPath, originalReceipt);
        var settingsPath = Path.Combine(root, PicDownloadSettings.RelativePath);
        var originalSettings = await File.ReadAllBytesAsync(settingsPath);
        await service.SaveSettingsAsync(root, "OTHER123", true);
        await Reject("changed programmer selection revokes old snapshot", ["PIC_DOWNLOAD_CHANGED"],
            () => service.DownloadApprovedAsync(root, preview.ApprovalId));
        Check((await Calls()).Length == 0, "changed serial cannot invoke the previously approved programmer");
        var missingProgrammer = await service.PreviewAsync(root);
        await Reject("missing selected programmer", ["PIC_IDENTIFY"],
            () => service.DownloadApprovedAsync(root, missingProgrammer.ApprovalId));
        await Record("missing-programmer", 0, 0);
        await File.WriteAllBytesAsync(settingsPath, originalSettings);
        var toolManifestPath = Path.Combine(component, "toolset.json");
        var originalToolManifest = await File.ReadAllBytesAsync(toolManifestPath);
        await Begin("success");
        await File.AppendAllTextAsync(toolManifestPath, "\n");
        await Reject("changed programming tool fingerprint", ["PIC_TOOL_CHANGED"],
            () => service.DownloadApprovedAsync(root, preview.ApprovalId));
        Check((await Calls()).Length == 0, "changed tool content cannot launch IPECMD using old settings");
        await File.WriteAllBytesAsync(toolManifestPath, originalToolManifest);

        var sourcePath = Path.Combine(root, "src/main.c");
        var originalSource = await File.ReadAllBytesAsync(sourcePath);
        foreach (var mutation in new[] { "source", "firmware", "snapshot", "settings" })
        {
            await Begin("success");
            var mutated = false;
            await Reject("changed " + mutation + " during preflight", ["PIC_BUILD_STALE", "PIC_DOWNLOAD_CHANGED"], () =>
                service.DownloadApprovedAsync(root, preview.ApprovalId, new InlineProgress(text =>
                {
                    if (!mutated && text.Contains("FIXTURE_CONFIG_READY", StringComparison.Ordinal))
                    {
                        mutated = true;
                        if (mutation == "source") { File.AppendAllText(sourcePath, "\n/* changed during preflight */\n"); }
                        if (mutation == "firmware") { File.AppendAllText(firmware.HexPath, "changed"); }
                        if (mutation == "snapshot") { File.AppendAllText(Path.Combine(root, ".build/pic-download/latest/firmware.hex"), "changed"); }
                        if (mutation == "settings") { File.WriteAllText(settingsPath, JsonSerializer.Serialize(preview.Settings with { ProgrammerSerial = "OTHER123" }, JsonStore.Options)); }
                    }
                })));
            Check(mutated, mutation + " mutation occurred while real child preflight was running");
            await Record("changed-" + mutation, 0, 0);
            await File.WriteAllBytesAsync(sourcePath, originalSource);
            await File.WriteAllBytesAsync(firmware.HexPath, originalHex);
            await File.WriteAllBytesAsync(settingsPath, originalSettings);
        }

        foreach (var mode in new[] { "no-verify", "error", "verify-fails-zero" })
        {
            await Begin(mode);
            var result = await service.DownloadApprovedAsync(root, preview.ApprovalId);
            Check(!result.Success && result.BackupPath is null && File.Exists(result.LogPath),
                mode + " preserves original diagnostics and cannot report successful verification");
            await Record(mode, 0, 1);
            Check((await Calls()).Length == 2, mode + " performs no automatic retry, readback or restoration");
        }
        foreach (var (mode, code) in new[] { ("program-wrong-device", "PIC_TARGET_ID"), ("program-wrong-serial", "PIC_PROGRAMMER_ID") })
        {
            await Begin(mode);
            await Reject(mode + " cannot create a successful download report", [code],
                () => service.DownloadApprovedAsync(root, preview.ApprovalId));
            await Record(mode, 0, 1);
            Check((await Calls()).Length == 2, mode + " performs no recovery programming");
        }
        foreach (var mutation in new[] { "source", "snapshot" })
        {
            await Begin("success");
            var mutated = false;
            await Reject("changed " + mutation + " during programming", ["PIC_BUILD_STALE", "PIC_DOWNLOAD_CHANGED"], () =>
                service.DownloadApprovedAsync(root, preview.ApprovalId, new InlineProgress(text =>
                {
                    if (!mutated && text.Contains("FIXTURE_PROGRAM_READY", StringComparison.Ordinal))
                    {
                        mutated = true;
                        if (mutation == "source") { File.AppendAllText(sourcePath, "\n/* changed during programming */\n"); }
                        else { File.AppendAllText(Path.Combine(root, ".build/pic-download/latest/firmware.hex"), "changed"); }
                    }
                })));
            Check(mutated, mutation + " mutation occurred during the programming child process");
            await Record("programming-changed-" + mutation, 0, 1);
            Check((await Calls()).Length == 2, "changed " + mutation + " after writing never triggers implicit restoration");
            await File.WriteAllBytesAsync(sourcePath, originalSource);
        }

        foreach (var mode in new[] { "preflight-cancel", "cancel" })
        {
            await Begin(mode);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                _ = await service.DownloadApprovedAsync(root, preview.ApprovalId, new InlineProgress(text =>
                {
                    if (text.Contains("FIXTURE_PREFLIGHT_WAITING", StringComparison.Ordinal) ||
                        text.Contains("FIXTURE_PROGRAMMING_WAITING", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                }), cancellation.Token);
                throw new InvalidOperationException("cancellation ignored: " + mode);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Check(true, mode + " propagates caller cancellation");
            }
            using (ProbeLease.Acquire()) { Check(true, mode + " releases the probe lease"); }
            using (ProjectBuildStorage.Acquire(root, "pic")) { Check(true, mode + " releases the project artifact lease"); }
            await Record(mode, 0, mode == "cancel" ? 1 : 0);
            if (mode == "cancel")
            {
                var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(home, "program.pid")), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    Check(process.HasExited, "cancelled programming child no longer runs");
                }
                catch (ArgumentException) { Check(true, "cancelled programming child is removed from the process table"); }
            }
        }
        Check(Directory.GetDirectories(Path.Combine(root, ".build/pic-download")).Select(Path.GetFileName).SequenceEqual(new[] { "latest" }),
            "repeated success, failures and cancellation keep only latest, with no generated backup directory");
        if (enumerateHardware)
        {
            _ = await services.PicDownloads.ListAsync();
        }
        await JsonStore.WriteAsync(Path.Combine(evidence, "result.json"), new
        {
            passed = true, hardwareWrite = false, enumerateHardware,
            stub = "separate child EXE copied into an isolated, hashed toolset; no physical USB access",
            successfulDownloadProcessCount = successCalls.Length,
            measuredSuccessfulStubMilliseconds = watch.ElapsedMilliseconds,
            timingScope = "offline fixture only; not a real PICkit3 throughput benchmark",
            checks, cases, root, firmware
        });
    }
}

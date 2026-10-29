namespace StudioX.PicValidation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Pic;
using StudioX.Foundation;

internal static class DownloadValidation
{
    public static async Task FixtureAsync(string[] arguments)
    {
        var home = arguments.Single(arg => arg.StartsWith("-Duser.home=", StringComparison.Ordinal))[12..];
        Directory.CreateDirectory(home);
        await File.AppendAllTextAsync(Path.Combine(home, "calls.jsonl"), JsonSerializer.Serialize(arguments) + "\n");
        var mode = File.Exists(Path.Combine(home, "mode.txt")) ? await File.ReadAllTextAsync(Path.Combine(home, "mode.txt")) : "success";
        if (arguments.Contains("-T"))
        {
            Console.WriteLine("Available Tool List\n-------------------\n1  PICkit3 S.No : FIXTURE123\n2  PICkit4 S.No : NOTPK3");
            Environment.ExitCode = 50;
            return;
        }
        if (arguments.SingleOrDefault(arg => arg.StartsWith("-GF", StringComparison.Ordinal)) is { } backup)
        {
            if (mode == "backup-fail")
            {
                Console.Error.WriteLine("Read failed: simulated transport failure");
                Environment.ExitCode = 3;
                return;
            }
            Console.WriteLine("Target device " + (mode == "wrong-device" ? "PIC16F876A" : "PIC16F877A") + " found.\nDevice Revision ID = 8");
            var image = await File.ReadAllBytesAsync(Path.Combine(home, "backup.hex"));
            await File.WriteAllBytesAsync(backup[3..], image);
            Console.WriteLine("Read successfully.\nOperation Succeeded");
            return;
        }
        if (arguments.Contains("-M"))
        {
            if (mode == "cancel")
            {
                Console.WriteLine("Programming started");
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
            if (mode == "error")
            {
                Console.Error.WriteLine("Verify failed: simulated mismatch");
                Environment.ExitCode = 7;
                return;
            }
            Console.WriteLine(mode == "no-verify" ? "Operation Succeeded" : mode == "verify-fails-zero" ? "Programming/Verify complete\nVerification failed.\nOperation Failed" : "Programming/Verify complete\nVerification successful.\nVerify Succeeded.\nOperation Succeeded");
            return;
        }
        throw new InvalidOperationException("Unexpected fixture invocation");
    }

    public static async Task RunAsync(string evidence, string runtime, string archive)
    {
        if (Directory.Exists(evidence))
        {
            throw new ArgumentException("Use new evidence directory");
        }
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        async Task Reject(string code, Func<Task> action)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Expected " + code);
            }
            catch (StudioXException error) when (error.Code == code) { Check(true, code + " rejected"); }
        }
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var pack = await services.Packs.ImportAsync(archive);
        var root = Path.Combine(evidence, "中文 PIC project");
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-gpio-4mhz", "PicDownloadTest", root);
        Check((await services.Builds.BuildAsync(root)).Success, "real XC8 GPIO image builds");
        var firmware = await services.Builds.Pic.PreviewAsync(root);
        var bytes = await File.ReadAllBytesAsync(firmware.HexPath);
        var image = PicHexImage.Validate(bytes);
        Check(image.ProgramWords > 0 && (image.ConfigurationWord & 0x2100) == 0x2100, "actual XC8 HEX has valid reset vector, 14-bit words and explicit unprotected config");
        Check(PicHexImage.Validate(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(evidence)!, "hardware-before.hex")), true).ProgramWords == 8192,
            "actual target backup including EEPROM and user IDs validates against PIC16F877A address map");
        foreach (var invalid in new[] { bytes[..^12], Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes).Replace(':', ';')),
            Encoding.ASCII.GetBytes(":020000000000FE\n:02400E00FF3F72\n:024200000000BC\n:00000001FF\n") })
        {
            await Reject("PIC_DOWNLOAD_IMAGE", () => Task.Run(() => PicHexImage.Validate(invalid)));
        }
        await Reject("PIC_DOWNLOAD_SETTINGS", () => Task.Run(() => new PicDownloadSettings(ProgrammerSerial: "../other").Validate(false)));
        var component = Path.Combine(evidence, "fixture-tools", "microchip.ipe", "6.20.0");
        var bin = Path.Combine(component, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }
        await File.WriteAllTextAsync(Path.Combine(component, "ipecmd.jar"), "fixture marker; no physical USB access");
        var hashes = Directory.GetFiles(component, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(component, path).Replace('\\', '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await JsonStore.WriteAsync(Path.Combine(component, "toolset.json"), new ToolsetManifest(1, "microchip.ipe", "6.20.0", "win-x64", "ipecmd-6.20",
            new()
            {
                ["java"] = "bin/StudioX.PicValidation.exe",
                ["ipecmd"] = "ipecmd.jar"
            }, hashes, "PICkit3 fixture",
            new()
            {
                ["ipe"] = "6.20"
            }, new()
            {
                ["ipe"] = "bin"
            }, "pic-ipe"));
        var fixtureData = Path.Combine(evidence, "fixture-data");
        var home = Path.Combine(fixtureData, "pic/ipe-user");
        Directory.CreateDirectory(home);
        var backupFile = Path.Combine(home, "backup.hex");
        await File.WriteAllBytesAsync(backupFile, bytes);
        var modeFile = Path.Combine(home, "mode.txt");
        var service = new StudioX.Engine.Pic.PicDownloadService(new ToolsetCatalog(Path.Combine(evidence, "fixture-tools")), services.Builds.Pic, fixtureData);
        var listed = await service.ListAsync();
        Check(listed is [{ Serial: "FIXTURE123" }], "enumeration excludes other PICkit types and does not guess a target");
        await service.SaveSettingsAsync(root, "FIXTURE123", true);
        var preview = await service.PreviewAsync(root);
        Check((await service.DownloadApprovedAsync(root, preview.ApprovalId)).Success, "approved image is backed up, programmed and verified using fixture transport");
        var calls = (await File.ReadAllLinesAsync(Path.Combine(home, "calls.jsonl"))).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
        var program = calls.Single(args => args.Contains("-M"));
        Check(program.Contains("-TSFIXTURE123") && program.Contains("-P16F877A") && program.Contains("-Z") && program.Contains("-OID") && program.Contains("-Y") && program.Contains("-OL") && !program.Any(arg => arg.StartsWith("-W", StringComparison.Ordinal)) && !program.Contains("-E"), "exact chip and serial selected, EEPROM/IDs preserved, external target power and explicit verify");
        await services.Builds.BuildAsync(root);
        await Reject("PIC_DOWNLOAD_CHANGED", () => service.DownloadApprovedAsync(root, preview.ApprovalId));
        preview = await service.PreviewAsync(root);
        foreach (var (mode, code) in new[] { ("wrong-device", "PIC_TARGET_ID"), ("backup-fail", "PIC_BACKUP") })
        {
            var before = (await File.ReadAllLinesAsync(Path.Combine(home, "calls.jsonl"))).Count(line => line.Contains("\"-M\"", StringComparison.Ordinal));
            await File.WriteAllTextAsync(modeFile, mode);
            await Reject(code, () => service.DownloadApprovedAsync(root, preview.ApprovalId));
            Check((await File.ReadAllLinesAsync(Path.Combine(home, "calls.jsonl"))).Count(line => line.Contains("\"-M\"", StringComparison.Ordinal)) == before, mode + " cannot launch programming");
        }
        await File.WriteAllTextAsync(modeFile, "success");
        await File.WriteAllTextAsync(backupFile, ":020000000000FE\n:02400E00FF1F92\n:00000001FF\n");
        await Reject("PIC_CODE_PROTECTED", () => service.DownloadApprovedAsync(root, preview.ApprovalId));
        await File.WriteAllBytesAsync(backupFile, bytes);
        foreach (var mode in new[] { "no-verify", "error", "verify-fails-zero" })
        {
            await File.WriteAllTextAsync(modeFile, mode);
            var result = await service.DownloadApprovedAsync(root, preview.ApprovalId);
            Check(!result.Success && File.Exists(result.LogPath) && File.Exists(result.BackupPath), mode + " retains backup and original diagnostics without reporting success");
        }
        await File.WriteAllTextAsync(modeFile, "cancel");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try
            {
                await service.DownloadApprovedAsync(root, preview.ApprovalId, token: cancel.Token);
                throw new InvalidOperationException("cancel ignored");
            }
            catch (OperationCanceledException) { Check(true, "cancellation stops the owned tool process and retains diagnostics"); }
        }
        using (ProbeLease.Acquire())
        {
            Check(true, "probe lease released after cancellation");
        }
        var current = await services.PicDownloads.ListAsync();
        Check(current.Any(item => item.Serial == "BUR195068601"), "real standalone managed runtime enumerates connected PICkit3 through application service");
        await services.PicDownloads.SaveSettingsAsync(root, "BUR195068601", true);
        var plan = await services.ProjectTools.InspectAsync(root);
        Check(plan.Requirements.Count == 2 && plan.Requirements.All(item => item.State == StudioX.Application.Tools.ProjectToolState.Installed), "compiler and programming runtime visible as separate installed project components");
        await JsonStore.WriteAsync(Path.Combine(evidence, "result.json"), new
        {
            passed = true,
            hardwareWrite = false,
            checks,
            root,
            firmware
        });
    }
}

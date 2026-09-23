using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class Ag32TargetChecks
{
    public static async Task<int> RunVerificationDiagnosticsAsync(string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
        Directory.CreateDirectory(root);
        var results = new List<string>();
        await CheckImageVerificationDiagnosticsAsync(root, results.Add);
        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results.Prepend("PASS — offline only; no USB or remote target connection"));
        return 0;
    }
    public static async Task<int> RunAsync(string archive, string runtime, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
        Directory.CreateDirectory(root);
        var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(archive);
        var catalog = new ToolsetCatalog(Path.GetFullPath(Path.Combine(runtime, "toolsets")));
        var downloads = new OpenOcdService(catalog);
        var builds = new BuildService(catalog);
        var results = new List<string>();
        void Pass(string message) { results.Add(message); Console.WriteLine("PASS " + message); }
        foreach (var template in pack.Manifest.Devices.Single().Templates)
        {
            var project = Path.Combine(root, template.Id);
            await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", template.Id, "AG32_Debug_Review", project);
            await using (var session = new DebugSessionService(Path.Combine(root, "settings")))
            {
                await session.OpenProjectAsync(project);
                Check(session.Watches.SequenceEqual(["$pc"]), "AG32 default watch avoids F407 simulation symbols");
            }
            var config = (await downloads.ConfigurationAsync(project))!;
            Check(config.Options.ProbeId == "agm-blaster" && config.OpenOcd.Probes.Count == 1 && config.OpenOcd.ApplicationFlashBytes == 0x27000, "Official probe and app boundary");
            Check((await builds.BuildAsync(project)).Success, "AG32 build: " + template.Id);
            var prepared = await HardwareDebugPreparer.PrepareAsync(project, downloads);
            var target = OpenOcdDebugPlanner.ResolveTarget(config);
            Check(target.IsAg32 && target.Core == "AgRV · RV32" && target.HasFpu, "AG32 target profile");
            var plan = OpenOcdDebugPlanner.Create(project, config, prepared.Tools, prepared.Elf, 43334);
            Check(plan.Gdb.Contains("riscv64-unknown-elf-gdb", StringComparison.Ordinal) && plan.OpenOcd.Contains("agm.agrv", StringComparison.Ordinal), "Vendor-specific tools");
            Check(plan.OpenOcdArguments.Any(c => c.Contains("studiox_ag32_detach", StringComparison.Ordinal)) && plan.OpenOcdArguments.All(c => !c.Contains("cortex_m", StringComparison.Ordinal)), "No Cortex-M commands sent to RISC-V");
            Check(plan.InitializeCommands.Any(c => c.Contains("verify_image", StringComparison.Ordinal)) && plan.InitializeCommands.All(c => !c.Contains("target-download", StringComparison.Ordinal)), "Verify before attaching without implicit download");
            Check(plan.OpenOcdArguments.Contains("$_TARGETNAME configure -work-area-size 0 -work-area-backup 1"), "No live RAM checksum workspace");
            foreach (var probe in new[] { "cmsis-dap", "stlink", "jlink" })
                await Reject(() => { OpenOcdDebugPlanner.ResolveProbe(config with { Options = new(probe, 1000) }); return Task.CompletedTask; }, "DEBUG_PROBE");
            var environment = ToolsetEnvironment.Create(prepared.Tools);
            var runner = new ProcessRunner();
            var dry = await runner.RunAsync(new(plan.OpenOcd, ["-c", "noinit", .. plan.OpenOcdArguments, "-f", Path.GetFullPath("tools/StudioX.DebugValidation/ag32-offline-check.cfg")], project,
                TimeSpan.FromSeconds(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "openocd-noinit.log"), dry.StandardOutput + dry.StandardError);
            Check(dry.Success && (dry.StandardOutput + dry.StandardError).Contains("AG32_OFFLINE_CHECKS_OK", StringComparison.Ordinal), "OpenOCD noinit guards / detach: " + dry.StandardOutput + dry.StandardError);
            var settings = plan.InitializeCommands.Where(c => c.StartsWith("-gdb-set ", StringComparison.Ordinal))
                .SelectMany(c => new[] { "-ex", "set " + c[9..] }).ToArray();
            var gdb = await runner.RunAsync(new(plan.Gdb, ["--nx", "--batch", .. settings, "-ex", "show architecture", "-ex", "info address main", prepared.Elf], project,
                TimeSpan.FromSeconds(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "gdb-symbols.log"), gdb.StandardOutput + gdb.StandardError);
            Check(gdb.Success && gdb.StandardOutput.Contains("riscv:rv32", StringComparison.Ordinal) && gdb.StandardOutput.Contains("main", StringComparison.Ordinal), "Bundled RISC-V GDB / ELF symbols");
            await JsonStore.WriteAsync(Path.Combine(project, "debug-plan.json"), plan);
            await ImageBoundaryChecks(project, config, downloads);
            Pass(template.Id + ": compiled, debug ELF prepared, vendor GDB symbols, noinit identity/layout/protection/detach guards, app/ELF boundaries; no hardware connection");
        }
        await RegisterChecks.RunRiscVAsync();
        Pass("RISC-V integer / PC / floating-point / CSR register names follow target MI response");
        await CheckImageVerificationDiagnosticsAsync(root, Pass);
        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results.Prepend("PASS — offline only; no USB or remote target connection"));
        return 0;
    }

    private static async Task CheckImageVerificationDiagnosticsAsync(string root, Action<string> pass)
    {
        var verifyLog = Path.Combine(root, "image-verify-diagnostics.log");
        var commandError = new StudioXException("GDB_COMMAND", "monitor command failed");
        await File.WriteAllTextAsync(verifyLog, "GDB > 7-interpreter-exec console \"monitor verify_image test.elf 0 elf\"\nOpenOCD < Error: error reading USB data\nGDB < 7^error");
        var usb = DebugSessionService.ExplainImageVerificationFailure(commandError, verifyLog);
        Check(usb.Code == "DEBUG_IMAGE_VERIFY_TRANSPORT" && usb.InnerException == commandError &&
              usb.Message.Contains("无法判断板上固件是否匹配", StringComparison.Ordinal) &&
              !usb.Message.Contains("请先使用“下载”", StringComparison.Ordinal), "USB read failure must not be reported as a firmware mismatch");
        await File.WriteAllTextAsync(verifyLog, "GDB > 7-interpreter-exec console \"monitor verify_image test.elf 0 elf\"\nOpenOCD < diff 0 address 0x80000012. Was 0xff instead of 0x03\nGDB < 7^error");
        var mismatch = DebugSessionService.ExplainImageVerificationFailure(commandError, verifyLog);
        Check(mismatch.Code == "DEBUG_IMAGE_MISMATCH" && mismatch.InnerException == commandError &&
              mismatch.Message.Contains("字节与当前 ELF 不一致", StringComparison.Ordinal), "Confirmed byte difference should have a distinct diagnosis");
        await File.WriteAllTextAsync(verifyLog, "GDB > 7-interpreter-exec console \"monitor verify_image test.elf 0 elf\"\nOpenOCD < diff 0 address 0x80000012. Was 0xff instead of 0x03\nOpenOCD < Error: error reading USB data\nGDB < 7^error");
        Check(DebugSessionService.ExplainImageVerificationFailure(commandError, verifyLog).Code == "DEBUG_IMAGE_VERIFY_TRANSPORT",
              "A byte difference during a failed USB transfer must not be treated as confirmed firmware mismatch");
        var uncertain = DebugSessionService.ExplainImageVerificationFailure(commandError, Path.Combine(root, "missing-verification.log"));
        Check(uncertain.Code == "DEBUG_IMAGE_VERIFY" && uncertain.InnerException == commandError &&
              uncertain.Message.Contains("无法判断", StringComparison.Ordinal), "Missing diagnostics must not infer a firmware mismatch");
        pass("AG32 verify_image diagnoses USB transport, confirmed byte differences and inconclusive errors separately");
    }

    private static async Task ImageBoundaryChecks(string project, DownloadConfiguration config, OpenOcdService downloads)
    {
        var receiptPath = Path.Combine(project, ".build/studiox-build-receipt.json");
        var receiptText = await File.ReadAllTextAsync(receiptPath);
        var receipt = JsonNode.Parse(receiptText)!;
        var binPath = Path.Combine(project, receipt["images"]![0]!["relativePath"]!.GetValue<string>());
        var elfPath = Path.Combine(project, receipt["images"]![0]!["symbolsPath"]!.GetValue<string>());
        var bin = await File.ReadAllBytesAsync(binPath); var elf = await File.ReadAllBytesAsync(elfPath);
        try
        {
            // 即使构建凭据摘要一致，超过 156 KiB 的 BIN 也不能覆盖逻辑区。
            var oversized = new byte[0x27001];
            await File.WriteAllBytesAsync(binPath, oversized);
            receipt["images"]![0]!["sha256"] = Convert.ToHexString(SHA256.HashData(oversized));
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString(JsonStore.Options));
            await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_IMAGE");
            await File.WriteAllBytesAsync(binPath, bin);
            receipt = JsonNode.Parse(receiptText)!;
            var wrong = elf.ToArray(); wrong[18] = 40; wrong[19] = 0;
            await File.WriteAllBytesAsync(elfPath, wrong);
            receipt["images"]![0]!["symbolsSha256"] = Convert.ToHexString(SHA256.HashData(wrong));
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString(JsonStore.Options));
            await Reject(() => HardwareDebugPreparer.PrepareAsync(project, downloads), "DOWNLOAD_IMAGE");
        }
        finally
        {
            await File.WriteAllBytesAsync(binPath, bin); await File.WriteAllBytesAsync(elfPath, elf);
            await File.WriteAllTextAsync(receiptPath, receiptText);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string code)
    {
        try { await action(); } catch (StudioXException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }
}

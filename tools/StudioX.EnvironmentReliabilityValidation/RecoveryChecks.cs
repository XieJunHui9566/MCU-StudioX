namespace StudioX.EnvironmentReliabilityValidation;

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;

internal static class RecoveryChecks
{
    private static readonly string[] checkpoints = ["组件事务已记录", "原组件已备份", "已校验组件已就位"];
    internal static async Task ChildAsync(string root, string archive, string checkpoint)
    {
        var service = new ToolEnvironmentService(new(root));
        await service.RepairAsync(new("test.recovery", "1.0.0", "Fixture", "test-gcc", 0, 0, false, ""), archive,
            new Callback(value =>
            {
                if (!value.StartsWith(checkpoints[int.Parse(checkpoint)], StringComparison.Ordinal))
                {
                    return;
                }
                Console.WriteLine("READY");
                Console.Out.Flush();
                Console.ReadLine();
            }));
    }
    internal static async Task DownloadChildAsync(string root, string source, long length, string hash)
    {
        var (listing, entry) = DownloadFixture(source, length, hash);
        using var service = new DistributionService(root);
        await service.DownloadAsync(listing, entry, new Callback(value =>
        {
            if (!value.StartsWith("下载 ", StringComparison.Ordinal))
            {
                return;
            }
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
        }));
    }
    internal static async Task RunAsync(string output, string ninja)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use new recovery evidence.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var passed = false;
        void Check(bool value, string text)
        {
            if (!value)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
            Console.WriteLine("PASS " + text);
        }
        async Task Reject(Func<Task> action, string code, string text)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Expected " + code);
            }
            catch (StudioXException error) when (error.Code == code) { Check(true, text); }
        }
        var source = Path.Combine(output, "source");
        var relative = "test.recovery/1.0.0";
        var original = Path.Combine(source, relative);
        Directory.CreateDirectory(Path.Combine(original, "include"));
        await File.WriteAllBytesAsync(Path.Combine(original, "probe.exe"), await File.ReadAllBytesAsync(ninja));
        await File.WriteAllTextAsync(Path.Combine(original, "include/device.h"), "#define RECOVERY_FIXTURE 1\n");
        var manifest = new ToolsetManifest(1, "test.recovery", "1.0.0", "win-x64", "test-gcc",
            new()
            {
                ["gcc"] = "probe.exe",
                ["gxx"] = "probe.exe",
                ["cmake"] = "probe.exe",
                ["ninja"] = "probe.exe",
                ["objcopy"] = "probe.exe",
                ["size"] = "probe.exe"
            },
            new()
            {
                ["probe.exe"] = Hash(await File.ReadAllBytesAsync(Path.Combine(original, "probe.exe"))),
                ["include/device.h"] = Hash(await File.ReadAllBytesAsync(Path.Combine(original, "include/device.h")))
            });
        await JsonStore.WriteAsync(Path.Combine(original, "toolset.json"), manifest);
        var archive = Path.Combine(output, "fixture.mcutoolchain");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var file in new[] { "toolset.json", "probe.exe", "include/device.h" })
            {
                zip.CreateEntryFromFile(Path.Combine(original, file), file, CompressionLevel.NoCompression);
            }
        }
        async Task<string> Fixture(string name, bool existing = true)
        {
            var root = Path.Combine(output, name, "runtime/toolsets");
            Directory.CreateDirectory(root);
            if (existing)
            {
                foreach (var file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
                {
                    var target = PathBoundary.Resolve(root, relative + "/" + Path.GetRelativePath(original, file).Replace('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            await Task.CompletedTask;
            return root;
        }
        try
        {
            foreach (var checkpoint in Enumerable.Range(0, 3))
            {
                var root = await Fixture("crash-" + checkpoint);
                await KillAtReadyAsync(["--repair-child", root, archive, checkpoint.ToString()]);
                var service = new ToolEnvironmentService(new(root));
                var preview = (await service.InspectRecoveryAsync()).Items.Single();
                Check(preview.Action == new[] { "keep", "finish", "confirm" }[checkpoint], "process termination at checkpoint " + checkpoint + " has an explicit recovery action");
                var report = await new ToolManagementService(new(root), new(Path.Combine(output, "packs")), new(output), output).InspectAsync(null);
                Check(report.Recoveries.Count == 1 && report.Versions.All(item => !item.SafeToManage), "pending transaction is visible and prevents conflicting component mutations");
                using (var occupied = ToolUsageLease.Acquire(Path.Combine(root, relative)))
                {
                    await Reject(() => service.RecoverAsync(preview), "TOOLS_BUSY", "active component lease prevents recovery");
                }
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    try
                    {
                        await service.RecoverAsync(preview, token: canceled.Token);
                        throw new InvalidOperationException("Cancellation ignored");
                    }
                    catch (OperationCanceledException) { Check((await service.InspectRecoveryAsync()).Items.Count == 1, "canceled recovery preserves pending evidence"); }
                }
                await service.RecoverAsync(preview);
                Check((await service.InspectRecoveryAsync()).Items.Count == 0 &&
                    (await new ToolsetCatalog(root).ResolveAsync(manifest.Id, manifest.Version, manifest.CompilerId)).Manifest.Id == manifest.Id,
                    "fresh service recovers verified component after process termination");
                Check(Directory.EnumerateFiles(Path.Combine(root, ".transactions/history"), "*.json").Count() == 1,
                    "completed recovery keeps durable history outside pending records");
                if (checkpoint != 0)
                {
                    Check(Directory.EnumerateDirectories(root, ".rollback-*").Any(), "original component backup remains after recovery");
                }
            }
            var emptyRoot = await Fixture("first-install", existing: false);
            await KillAtReadyAsync(["--repair-child", emptyRoot, archive, "0"]);
            var emptyService = new ToolEnvironmentService(new(emptyRoot));
            var emptyPreview = (await emptyService.InspectRecoveryAsync()).Items.Single();
            Check(emptyPreview.Action == "finish" && !emptyPreview.CanRestorePrevious, "interrupted first installation never invents an old version");
            await emptyService.RecoverAsync(emptyPreview);
            Check(File.Exists(Path.Combine(emptyRoot, relative, "probe.exe")), "interrupted first installation can finish from fully verified staging");

            var missingManifest = await Fixture("missing-old-manifest");
            File.Delete(Path.Combine(missingManifest, relative, "toolset.json"));
            await KillAtReadyAsync(["--repair-child", missingManifest, archive, "0"]);
            var missingService = new ToolEnvironmentService(new(missingManifest));
            var missingPreview = (await missingService.InspectRecoveryAsync()).Items.Single();
            Check(missingPreview.Action == "keep", "interrupted repair of an already missing manifest can explicitly retain the old directory");
            await missingService.RecoverAsync(missingPreview);
            await missingService.RepairAsync(new(manifest.Id, manifest.Version, "Fixture", manifest.CompilerId, 0, 0, false, ""), archive, null);
            Check((await new ToolsetCatalog(missingManifest).ResolveAsync(manifest.Id, manifest.Version, manifest.CompilerId)).Manifest.Id == manifest.Id,
                "retained damaged original does not trap later explicit offline repair behind a pending transaction");

            var corrupted = await Fixture("corrupt-candidate");
            await KillAtReadyAsync(["--repair-child", corrupted, archive, "1"]);
            var corruptService = new ToolEnvironmentService(new(corrupted));
            var corruptPreview = (await corruptService.InspectRecoveryAsync()).Items.Single();
            var stage = Directory.EnumerateDirectories(corrupted, ".repair-*").Single();
            await File.WriteAllTextAsync(Path.Combine(stage, relative, "include/device.h"), "damaged");
            await Reject(() => corruptService.RecoverAsync(corruptPreview), "TOOL_HASH", "corrupted staging cannot be published despite unchanged manifest");
            Check(!Directory.Exists(Path.Combine(corrupted, relative)), "failed verification never publishes a partial component");
            await corruptService.RestorePreviousAsync(corruptPreview);
            Check((await new ToolsetCatalog(corrupted).ResolveAsync(manifest.Id, manifest.Version, manifest.CompilerId)).Manifest.Id == manifest.Id,
                "explicit previous-version restoration survives a damaged new candidate");

            var changed = await Fixture("changed-record");
            await KillAtReadyAsync(["--repair-child", changed, archive, "1"]);
            var changedService = new ToolEnvironmentService(new(changed));
            var changedPreview = (await changedService.InspectRecoveryAsync()).Items.Single();
            var record = Directory.EnumerateFiles(Path.Combine(changed, ".transactions"), "*.json").Single();
            await File.AppendAllTextAsync(record, "\n");
            await Reject(() => changedService.RecoverAsync(changedPreview), "TOOLS_RECOVERY_CHANGED", "record changes invalidate an existing recovery preview");
            var refreshed = (await changedService.InspectRecoveryAsync()).Items.Single();
            await changedService.RecoverAsync(refreshed);
            var movable = await Fixture("journal-before");
            await KillAtReadyAsync(["--repair-child", movable, archive, "1"]);
            var movedRoot = Path.Combine(output, "journal after move", "runtime/toolsets");
            if (!Path.GetFullPath(movable).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(movedRoot).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Recovery move escaped evidence.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(movedRoot)!);
            Directory.Move(movable, movedRoot);
            var movedService = new ToolEnvironmentService(new(movedRoot));
            await movedService.RecoverAsync((await movedService.InspectRecoveryAsync()).Items.Single());
            Check((await new ToolsetCatalog(movedRoot).ResolveAsync(manifest.Id, manifest.Version, manifest.CompilerId)).Manifest.Id == manifest.Id,
                "relative transaction records still recover after the installation itself moves");
            var wrongLeaseRoot = await Fixture("maintenance-lease");
            using (var use = ToolUsageLease.Acquire(Path.Combine(wrongLeaseRoot, relative)))
            {
                await Reject(() => new ToolsetCatalog(wrongLeaseRoot).VerifyForMaintenanceAsync(manifest.Id, manifest.Version, manifest.CompilerId, use), "TOOLS_BUSY",
                "shared use lease cannot bypass exclusive maintenance verification");
            }
            using (var wrong = ToolUsageLease.Acquire(Path.Combine(output, "other-directory"), maintenance: true))
            {
                await Reject(() => new ToolsetCatalog(wrongLeaseRoot).VerifyForMaintenanceAsync(manifest.Id, manifest.Version, manifest.CompilerId, wrong), "TOOLS_BUSY",
                "maintenance lease cannot authorize another component directory");
            }

            var localArchive = Path.Combine(output, "download-source.mcutoolchain");
            var payload = Enumerable.Range(0, 2 * 1024 * 1024 + 37).Select(i => (byte)(i * 17)).ToArray();
            await File.WriteAllBytesAsync(localArchive, payload);
            var downloadRoot = Path.Combine(output, "download-process");
            await KillAtReadyAsync(["--download-child", downloadRoot, localArchive, payload.Length.ToString(), Hash(payload)]);
            using var download = new DistributionService(downloadRoot);
            var (listing, entry) = DownloadFixture(localArchive, payload.Length, Hash(payload));
            Check(download.DownloadState(entry).SavedBytes == 1024 * 1024, "terminated download preserves the exact flushed progress boundary");
            var result = await download.DownloadAsync(listing, entry);
            Check((await File.ReadAllBytesAsync(result)).SequenceEqual(payload), "fresh process completes interrupted offline transfer and verifies the whole archive");
            await File.WriteAllBytesAsync(result, payload.Select(b => (byte)(b ^ 1)).ToArray());
            result = await download.DownloadAsync(listing, entry);
            Check((await File.ReadAllBytesAsync(result)).SequenceEqual(payload), "same-size corrupted complete cache is reverified and repaired");
            passed = true;
        }
        finally { await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed, checks, hardware = false, downloadedSdk = false }); }
    }
    private static async Task KillAtReadyAsync(string[] arguments)
    {
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            string? line;
            do
            {
                line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            } while (line is not null && line != "READY");
            if (line != "READY")
            {
                throw new IOException("Child did not reach the transaction boundary: " + await stderr);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            // Windows 在进程终止通知后可能仍短暂释放文件对象；只等待明确的子进程租约，不绕过真实占用。
            if (arguments[0] == "--repair-child")
            {
                for (var attempt = 0; attempt < 50 && ToolUsageLease.IsBusy(Path.Combine(arguments[1], "test.recovery/1.0.0")); attempt++)
                {
                    await Task.Delay(40);
                }
            }
        }
    }
    private static (DistributionListing, DistributionEntry) DownloadFixture(string source, long length, string hash)
    {
        var entry = new DistributionEntry("tool", "test.download", "1.0.0", "Fixture", Path.GetFileName(source), hash, length, length,
            "NOASSERTION", "https://example.test/source", "Offline recovery fixture");
        return (new(new(1, "Fixture", [entry]), Path.Combine(Path.GetDirectoryName(source)!, "catalog.json"), "local", "fixture"), entry);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class Callback(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
}

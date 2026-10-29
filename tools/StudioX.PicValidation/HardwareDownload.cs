namespace StudioX.PicValidation;

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Pic;
using StudioX.Foundation;

/// <summary>人工明确授权后才调用；绑定展示过的测试固件及原程序摘要，结束后恢复原程序。</summary>
internal static class HardwareDownload
{
    public static async Task RunAsync(string evidence, string runtime, string root, string serial, string approvedHex, string original)
    {
        if (Directory.Exists(evidence))
        {
            throw new ArgumentException("Use a new hardware evidence directory");
        }
        Directory.CreateDirectory(evidence);
        var originalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original)));
        var originalMemory = MemoryFingerprint(await File.ReadAllBytesAsync(original));
        _ = PicHexImage.Validate(await File.ReadAllBytesAsync(original), true);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        await services.PicDownloads.SaveSettingsAsync(root, serial, false);
        var preview = await services.PicDownloads.PreviewAsync(root);
        if (preview.Firmware.Sha256 != approvedHex || preview.Image.ConfigurationWord != 0x3f32 || serial != "BUR195068601")
        {
            throw new InvalidOperationException("Approved hardware firmware, original configuration or programmer differs");
        }
        var started = false;
        string? backup = null;
        string? backupHash = null;
        var previousBackups = Directory.Exists(Path.Combine(root, ".build/pic-download"))
            ? Directory.GetFiles(Path.Combine(root, ".build/pic-download"), "before.hex", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        PicDownloadReport? report = null;
        ProcessResult? restored = null;
        try
        {
            report = await services.PicDownloads.DownloadApprovedAsync(root, preview.ApprovalId, new InlineProgress(text =>
            {
                Console.Write(text);
                if (text.Contains("开始写入与校验", StringComparison.Ordinal))
                {
                    backup = Directory.GetFiles(Path.Combine(root, ".build/pic-download"), "before.hex", SearchOption.AllDirectories).Single(path => !previousBackups.Contains(path));
                    var read = File.ReadAllBytes(backup);
                    if (MemoryFingerprint(read) != originalMemory)
                    {
                        throw new InvalidOperationException("Target original differs from the reviewed backup; programming blocked");
                    }
                    backupHash = Convert.ToHexString(SHA256.HashData(read));
                    started = true;
                }
            }));
        }
        finally
        {
            if (started && backup is not null)
            {
                var tools = await services.Toolsets.ResolveAsync("microchip.ipe", "6.20.0", "ipecmd-6.20");
                using var probe = ProbeLease.Acquire();
                using var lease = ToolUsageLease.Acquire(tools.RootDirectory);
                if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backup))) != backupHash)
                {
                    throw new InvalidOperationException("Original backup changed; automatic restoration blocked");
                }
                var working = Path.Combine(evidence, "restore-user");
                Directory.CreateDirectory(working);
                restored = await new ProcessRunner().RunAsync(new(tools.Tool("java"),
                    ["-Duser.home=" + working, "-Duser.language=en", "-Duser.country=US", "-Dfile.encoding=UTF-8", "-jar", tools.Tool("ipecmd"),
                    "-P16F877A", "-TPPK3", "-TS" + serial, "-F" + backup, "-Z", "-OID", "-M", "-Y", "-OL"], working, TimeSpan.FromMinutes(10),
                    ToolsetEnvironment.Create(tools), RemoveEnvironment: [.. ToolsetEnvironment.AmbientVariables, "JAVA_HOME", "JRE_HOME", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH"],
                    Output: new InlineProgress(Console.Write), StreamCompleteOutput: true, ContainProcessTree: true));
                await File.WriteAllTextAsync(Path.Combine(evidence, "restore.log"), restored.StandardOutput + "\n" + restored.StandardError);
                await JsonStore.WriteAsync(Path.Combine(evidence, "restore.json"), restored);
            }
            await JsonStore.WriteAsync(Path.Combine(evidence, "result.json"), new
            {
                preview,
                original,
                originalHash,
                originalMemory,
                backup,
                backupHash,
                started,
                report,
                restored
            });
        }
        if (report?.Success != true || restored?.Success != true || !Regex.IsMatch(restored.StandardOutput, @"(?im)^\s*Verification successful\.\s*$"))
        {
            throw new InvalidOperationException("Hardware programming or original restoration not verified; inspect preserved raw logs");
        }
    }

    private static string MemoryFingerprint(byte[] image)
    {
        _ = PicHexImage.Validate(image, true);
        var bytes = new SortedDictionary<uint, byte>();
        uint upper = 0;
        foreach (var line in System.Text.Encoding.ASCII.GetString(image).Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0))
        {
            var record = Convert.FromHexString(line[1..]);
            if (record[3] is 2 or 4)
            {
                upper = (uint)(record[4] * 256 + record[5]) << (record[3] == 2 ? 4 : 16);
            }
            if (record[3] != 0)
            {
                continue;
            }
            var address = upper + (uint)(record[1] * 256 + record[2]);
            for (uint index = 0; index < record[0]; index++)
            {
                bytes.Add(address + index, record[4 + index]);
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (address, value) in bytes)
        {
            hash.AppendData(BitConverter.GetBytes(address));
            hash.AppendData([value]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

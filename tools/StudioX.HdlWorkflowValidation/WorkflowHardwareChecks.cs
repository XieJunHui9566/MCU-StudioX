using System.Buffers.Binary;
using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

/// <summary>仅用于已授权的 CCT6 内部回环夹具；显式参数才会访问已识别的 DAP。</summary>
internal static class WorkflowHardwareChecks
{
    internal static async Task RunAsync(string runtime, string root, string output)
    {
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var downloads = new OpenOcdService(catalog);
        var options = new DownloadOptions("cmsis-dap", 1000, "0602A0002016");
        var preview = await downloads.PreviewAsync(root, options);
        if (preview.Configuration.Device.Id != "AG32VF303CCT6" || preview.Images.Count != 2)
        {
            throw new InvalidOperationException("验收必须是 CCT6 的双镜像内部回环工程。");
        }
        var main = await File.ReadAllTextAsync(Path.Combine(root, "src/main.c"));
        if (!main.Contains("logic_test"))
        {
            throw new InvalidOperationException("缺少已核实的内部回环固件。");
        }
        var image = await new Ag32NativeBuildService(catalog).RequireImageAsync(root);
        var tools = await catalog.ResolveAsync("agm.agrv", "1.0.0", "agrv-gcc-11.1.0");
        var backup = Path.Combine(output, "joint-flash-before.bin");
        if (File.Exists(backup))
        {
            throw new InvalidOperationException("硬件备份已经存在，不覆盖。");
        }
        await ReadBoardAsync("before", $"dump_image {{{backup.Replace('\\', '/')}}} 0x80000000 0x40000; mdw 0x81000030 2; resume");
        var backupHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(backup)));
        if (new FileInfo(backup).Length != 262144)
        {
            throw new InvalidOperationException("完整 Flash 备份失败。");
        }
        var report = await downloads.DownloadApprovedAsync(root, options, preview.Configuration.Device.Id, preview.ApprovalSha256);
        await File.WriteAllTextAsync(Path.Combine(output, "joint-download-summary.txt"), report.Summary);
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Summary + " " + report.LogPath);
        }
        await Task.Delay(1500);
        var first = await CaptureAsync("after-first");
        await Task.Delay(1500);
        var second = await CaptureAsync("after-running");
        if (first[4] != 0 || second[4] != 0 || second[3] <= first[3] || second[1] != 0x40200001)
        {
            throw new InvalidOperationException("联合下载后回环不通过，请检查原始日志。");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "hardware-result.json"), new
        {
            device = preview.Configuration.Device.Id,
            probe = options,
            backup,
            backupHash,
            images = preview.Images,
            download = report.LogPath,
            first,
            second,
            passed = "双镜像分别写入校验；复位后内部回环持续增长且 errors 为 0；选项字节保留"
        });
        Console.WriteLine($"PASS joint hardware: checks {first[3]} -> {second[3]}, errors {second[4]}, FPGA {image.ByteCount} bytes");

        async Task<uint[]> CaptureAsync(string name)
        {
            var file = Path.Combine(output, name + ".bin");
            await ReadBoardAsync(name, $"dump_image {{{file.Replace('\\', '/')}}} 0x2000010c 44; mdw 0x81000030 2; resume");
            var bytes = await File.ReadAllBytesAsync(file);
            return Enumerable.Range(0, 11).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4))).ToArray();
        }
        async Task ReadBoardAsync(string name, string command)
        {
            var args = new[] { "-s", tools.ResourceDirectory("openocdScripts"), "-f", "interface/cmsis-dap.cfg", "-c",
                "adapter serial 0602A0002016; transport select swd; adapter speed 1000; gdb port disabled; tcl port disabled; telnet port disabled",
                "-f", Path.Combine(root, "device/debug/ag32vf303.cfg"), "-c", "init; halt; studiox_check_target; " + command + "; shutdown" };
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("openocd"), args, root, TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(tools)));
            await File.WriteAllTextAsync(Path.Combine(output, "joint-" + name + ".log"), result.StandardOutput + result.StandardError);
            if (!result.Success)
            {
                throw new InvalidOperationException("硬件读取失败：" + name);
            }
        }
    }
}

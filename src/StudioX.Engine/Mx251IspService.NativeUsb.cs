namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

public sealed partial class Mx251IspService
{
    // 调用方已持有工程、CLI 输出和下载会话租约。
    private async Task<Mx251IspReport> DownloadNativeUsbAsync(Mx251IspPreparation prepared, StudioX.Ai8051Isp.FirmwareImage image,
        IProgress<string>? output, CancellationToken token)
    {
        using var ownership = ProbeLease.Acquire();
        using var log = new StreamWriter(new FileStream(prepared.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        long sequence = 0;
        void Event(object value)
        {
            log.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, hostUtc = DateTimeOffset.UtcNow, value }));
            output?.Report(JsonSerializer.Serialize(value));
        }
        try
        {
            Event(new { stage = "waiting", message = "请关闭官方 ISP，使 AI8051U 进入硬件 USB ISP 模式并连接芯片 USB；无需 COM，可点击停止取消。", firmwareSha256 = image.Sha256 });
            var timer = System.Diagnostics.Stopwatch.StartNew();
            HidDevice device;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var matches = backend.EnumerateNativeUsb().Where(d => d.VendorId == 0x34bf && d.ProductId == 0x1001 &&
                    d.InputBytes == 65 && d.OutputBytes == 65 && d.Path.Contains("vid_34bf&pid_1001", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw Invalid("发现多个原生 USB ISP 设备，请只保留本次目标板进入下载模式。");
                if (matches.Length == 1) { device = matches[0]; break; }
                if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw Invalid("等待原生 USB ISP 超时，请检查下载模式、数据线和供电。");
                await Task.Delay(200, token);
            }
            Event(new { stage = "open", device, transport = "native-usb" });
            using var connection = backend.Open(device, (direction, bytes) =>
                log.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, hostUtc = DateTimeOffset.UtcNow, direction, hex = Convert.ToHexString(bytes) })));
            var protocol = new Mx251NativeUsbProtocol(connection.Transport, Event);
            await protocol.IdentifyAsync(token);
            // 等待上电期间可能有外部编辑；只在等待之后、首次擦写之前复核一次。
            var current = await ReadBuildAsync(prepared.ProjectRoot, token);
            if (current.ReceiptHash != prepared.ReceiptSha256 || Hash(current.Image) != prepared.ImageSha256 ||
                await LoadSettingsAsync(prepared.ProjectRoot, token) != prepared.Settings)
                throw Invalid("等待 USB 期间工程或下载设置已改变，请重新下载。");
            var result = await protocol.ProgramAsync(image.Code, prepared.Settings, token);
            var report = new Mx251IspReport(true, $"原生 USB 下载完成：全部写块和 32 位配置已 ACK，保留芯片 IRC {result.Target.IrcHz / 1000000m:0.######} MHz，已发送运行命令。未进行配置/Flash 回读或程序功能验证。", prepared.LogPath, result);
            Event(new { stage = "complete", result });
            await JsonStore.WriteAsync(Path.Combine(Path.GetDirectoryName(prepared.ImagePath)!, "result.json"), report, CancellationToken.None);
            return report;
        }
        catch (Exception error) { Event(new { stage = "session-failed", error = error.ToString() }); throw; }
    }
}


namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

public sealed partial class Mx251IspService
{
    private async Task<Mx251IspReport> DownloadSerialAsync(Mx251IspPreparation prepared, StudioX.Ai8051Isp.FirmwareImage image,
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
            Event(new { stage = "open", port = prepared.Settings.SerialPort, transport = "serial", firmwareSha256 = image.Sha256 });
            using var connection = serialBackend.Open(prepared.Settings.SerialPort, (direction, bytes) =>
                log.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, hostUtc = DateTimeOffset.UtcNow, direction, hex = Convert.ToHexString(bytes) })));
            var protocol = new Mx251SerialProtocol(connection, Event);
            await protocol.IdentifyAsync(token);
            var current = await ReadBuildAsync(prepared.ProjectRoot, token);
            if (current.ReceiptHash != prepared.ReceiptSha256 || Hash(current.Image) != prepared.ImageSha256 ||
                await LoadSettingsAsync(prepared.ProjectRoot, token) != prepared.Settings)
                throw Invalid("等待上电期间工程或设置已改变，请重新下载。");
            var result = await protocol.ProgramAsync(image.Code, prepared.Settings, token);
            var report = new Mx251IspReport(true, "串口下载完成：全部写块和 32 位 IRC 配置已 ACK，已发送运行命令。未进行配置/Flash 回读或程序功能验证。", prepared.LogPath, result);
            Event(new { stage = "complete", result });
            await JsonStore.WriteAsync(Path.Combine(Path.GetDirectoryName(prepared.ImagePath)!, "result.json"), report, CancellationToken.None);
            return report;
        }
        catch (Exception error) { Event(new { stage = "session-failed", error = error.ToString() }); throw; }
    }
}


using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Engine.StcUsb;

/// <summary>仅在用户选定同一份官方测试固件后手工运行；默认离线入口不会调用。</summary>
internal static class NativeUsbHardwareCheck
{
    public static async Task RunAsync(string image, string log)
    {
        const string expectedSha256 = "D061365C8B632DF45F69FA2BD1713F7AD26F9F8E2D1803DC849FBD73E9F5DE07";
        var bytes = await File.ReadAllBytesAsync(image);
        if (bytes.Length != 384 || Convert.ToHexString(SHA256.HashData(bytes)) != expectedSha256)
        {
            throw new InvalidOperationException("只接受用户此前在官方工具下载的同一份 384 B 测试映像。");
        }
        var backend = new StcUsbBackend();
        using var lease = ProbeLease.Acquire();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var writer = new StreamWriter(new FileStream(Path.GetFullPath(log), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Write(string text)
        {
            writer.Write(text);
            Console.Write(text);
        }
        Write($"imageSha256={expectedSha256}\nSettings=preserve current clock and options\nWaiting for STC8H8K64U native USB bootloader…\n");
        try
        {
            while (true)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var matches = backend.Enumerate();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException("多个 USB ISP 设备，拒绝选择。");
                }
                if (matches.Count == 1)
                {
                    using var connection = backend.Open(matches[0], (direction, data) => writer.WriteLine($"{DateTimeOffset.UtcNow:O} {direction} {Convert.ToHexString(data)}"));
                    var protocol = new StcUsbProtocol(connection.Transport);
                    await protocol.IdentifyAsync(cancel.Token);
                    Write("STC8H8K64U / ISP 7.4.12U verified\n");
                    await protocol.ProgramAsync(bytes, new(DownloadMode: StcDownloadMode.NativeUsb), new SynchronousOutput(Write), cancel.Token);
                    Write("PASS native protocol hardware write and option ACK; no Flash readback or runtime qualification.\n");
                    return;
                }
                await Task.Delay(200, cancel.Token);
            }
        }
        catch (Exception error) { Write(error + "\n"); throw; }
    }

    private sealed class SynchronousOutput(Action<string> output) : IProgress<string>
    {
        public void Report(string value) => output(value);
    }
}

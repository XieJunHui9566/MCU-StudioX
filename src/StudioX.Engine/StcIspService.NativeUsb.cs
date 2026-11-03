namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using StudioX.Ai8051Isp;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;

public sealed partial class StcIspService
{
    private async Task<StcIspPreparation> PrepareNativeUsbAsync(PreviewInputs inputs, CancellationToken token)
    {
        var preview = inputs.Preview;
        using var storage = ProjectBuildStorage.Acquire(preview.ProjectRoot, "stc-isp");
        var session = storage.Prepare("stc-usb");
        var image = Path.Combine(session, "firmware.hex");
        await File.WriteAllBytesAsync(image, inputs.Image, token);
        return new(preview.ProjectRoot, preview.ExpectedModel, preview.ExpectedCodeBytes, "USB-AUTO", preview.SourceImage,
            image, preview.ImageSha256, preview.DataBytes, preview.HighestAddress, "", "", Path.Combine(session, "usb.log"),
            preview.Settings, ClockConfigurationSha256: inputs.ClockConfigurationSha256);
    }

    private async Task<byte[]> VerifyNativeUsbAsync(StcIspPreparation prepared, CancellationToken token)
    {
        var current = await PreviewCoreAsync(prepared.ProjectRoot, prepared.Settings, token);
        var expected = PathBoundary.Resolve(prepared.ProjectRoot, ".build/stc-usb/latest");
        if (prepared.MonitorSetup || prepared.ExpectedModel != "STC8H8K64U" || prepared.ExpectedCodeBytes != StcUsbProtocol.CodeBytes ||
            prepared.Port != "USB-AUTO" || prepared.PythonExecutable.Length != 0 || prepared.GuardScript.Length != 0 ||
            prepared.SourceImage != current.Preview.SourceImage || prepared.ImageSha256 != current.Preview.ImageSha256 ||
            prepared.ClockConfigurationSha256 != current.ClockConfigurationSha256 ||
            prepared.Image != Path.Combine(expected, "firmware.hex") || prepared.LogPath != Path.Combine(expected, "usb.log") ||
            !File.Exists(prepared.Image) || (File.GetAttributes(prepared.Image) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("STC_USB_PREPARE", "USB 下载准备、固件、时钟或目标与当前工程不一致；未写入。");
        }
        var bytes = await File.ReadAllBytesAsync(prepared.Image, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != prepared.ImageSha256)
        {
            throw new StudioXException("STC_USB_PREPARE", "USB 固件快照被修改；未写入。");
        }
        var image = StcIntelHex.Validate(bytes, StcUsbProtocol.CodeBytes);
        if (image.DataBytes != prepared.DataBytes || image.HighestAddress != prepared.HighestAddress)
        {
            throw new StudioXException("STC_USB_PREPARE", "USB 固件范围与准备记录不符。");
        }
        return image.Binary;
    }

    private async Task<StcIspReport> DownloadNativeUsbAsync(StcIspPreparation prepared, IProgress<string>? output, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("STC_ISP_BUSY", "STC 下载正在进行。");
        }
        try
        {
            using var storage = ProjectBuildStorage.Acquire(prepared.ProjectRoot, "stc-isp");
            _ = await VerifyNativeUsbAsync(prepared, token);
            using var ownership = ProbeLease.Acquire();
            var capture = new DownloadOutput(new StringBuilder(), output);
            try
            {
                capture.Report($"STC 原生 USB · {prepared.ExpectedModel}\n固件：{prepared.SourceImage}\nSHA-256：{prepared.ImageSha256}\n");
                capture.Report("StudioX: waiting for USB · 请使 MCU 进入硬件 USB ISP 模式后插入电脑；无需选择 COM 或固定 USB 插口。可点击停止取消等待。\n");
                var device = await WaitForNativeUsbAsync(token);
                capture.Report("Protocol detected: STC native USB HID\n设备：" + device.Product + "\n");
                using var connection = nativeUsb.Open(device, (direction, report) =>
                    capture.Raw($"{DateTimeOffset.UtcNow:O} USB {direction} {Convert.ToHexString(report)}\n"));
                var protocol = new StcUsbProtocol(connection.Transport);
                await protocol.IdentifyAsync(token);
                capture.Report("STUDIOX_TARGET_VERIFIED STC8H8K64U\n");
                // 等待插板期间设置或源码可能已变化，型号核对后、第一次擦除前再次检查完整构建凭据。
                var image = await VerifyNativeUsbAsync(prepared, token);
                await protocol.ProgramAsync(image, prepared.Settings, capture, token);
                return new(true, capture.Text, prepared.LogPath, 0, false, true);
            }
            catch (OperationCanceledException)
            {
                capture.Report("USB 下载已取消；若已开始擦写，板内程序可能不完整。\n");
                throw;
            }
            catch (Exception error)
            {
                capture.Report(error + "\n");
                throw;
            }
            finally { await File.WriteAllTextAsync(prepared.LogPath, capture.Text, CancellationToken.None); }
        }
        finally { gate.Release(); }
    }

    private async Task<HidDevice> WaitForNativeUsbAsync(CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var matches = nativeUsb.Enumerate().Where(device => device.VendorId == 0x34bf && device.ProductId == 0x1001 &&
                device.InputBytes == 65 && device.OutputBytes == 65 &&
                device.Path.Contains("vid_34bf&pid_1001", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1)
            {
                throw new StudioXException("STC_USB_MULTIPLE", "发现多个原生 USB ISP 设备；请只保留本次目标板处于下载模式，再点击下载，不会自动选择并写入其中一块。");
            }
            if (matches.Length == 1)
            {
                return matches[0];
            }
            if (timer.Elapsed >= nativeUsbTimeout)
            {
                throw new StudioXException("STC_USB_NOT_FOUND", "等待原生 USB ISP 设备超时；请核对下载模式、数据线和供电后重试。");
            }
            await Task.Delay(200, token);
        }
    }
}

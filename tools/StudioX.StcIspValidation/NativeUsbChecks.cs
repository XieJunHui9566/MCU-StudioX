using System.Buffers.Binary;
using System.Text.Json;
using StudioX.Ai8051Isp;
using StudioX.Engine;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;
using StudioX.Packages;

internal static class NativeUsbChecks
{
    private sealed record Fixture(string Name, bool Rc24MHz, string[] Tx, string[] Rx, string Binary);

    public static async Task RunAsync(string runtime, string archive, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
        {
            throw new InvalidOperationException("Use a new evidence directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(
            "tools/StudioX.StcIspValidation/fixtures/stc8h-usb.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        await NativeUsbOptionChecks.RunAsync(Check);
        var settings = new StcIspSettings(DownloadMode: StcDownloadMode.NativeUsb);
        foreach (var fixture in fixtures)
        {
            var transport = new Replay(fixture.Rx);
            var protocol = new StcUsbProtocol(transport);
            await protocol.IdentifyAsync(default);
            var selected = fixture.Rc24MHz ? settings with
            {
                ClockMode = StcClockMode.InternalRc,
                ClockFrequencyHz = 24000000
            } : settings;
            var progress = new Output();
            await protocol.ProgramAsync(Convert.FromHexString(fixture.Binary), selected, progress, default);
            Check(transport.Writes.Select(Convert.ToHexString).SequenceEqual(fixture.Tx),
                fixture.Name + " exact official Windows HID report replay, including padding and skipped FF page");
            Check(transport.Remaining == 0, fixture.Name + " every expected ACK consumed");
            Check(progress.Lines.Contains(fixture.Binary.Length == 256 ? "STUDIOX_ISP_PROGRESS 128 128\n" : "STUDIOX_ISP_PROGRESS 256 256\n"),
                fixture.Name + " progress counts only acknowledged write bytes, excluding erased FF pages");
        }
        var baseline = fixtures[1];
        var variant = new Replay(ChangeStatus(baseline.Rx, 26, 0x42));
        var variantProtocol = new StcUsbProtocol(variant);
        await variantProtocol.IdentifyAsync(default);
        await variantProtocol.ProgramAsync(Convert.FromHexString(baseline.Binary), settings with
        {
            ClockMode = StcClockMode.InternalRc,
            ClockFrequencyHz = 24000000
        }, null, default);
        Check(variant.Writes[^2][39] == 0x42, "RC24 options use this chip's calibration slot instead of captured sample constant");
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 6 })
        {
            var rx = baseline.Rx.ToArray();
            var bad = Convert.FromHexString(rx[index]);
            bad[6] ^= 1;
            rx[index] = Convert.ToHexString(bad);
            var transport = new Replay(rx);
            await RejectAsync(async () =>
            {
                var protocol = new StcUsbProtocol(transport);
                await protocol.IdentifyAsync(default);
                await protocol.ProgramAsync(Convert.FromHexString(baseline.Binary), settings, null, default);
            }, "STC_USB_PROTOCOL");
            Check(!transport.Writes.Any(IsExit), $"bad checksum at ACK {index} cannot finish/run");
        }
        foreach (var offset in new[] { 0, 1, 2, 3, 4, 5, 62 })
        {
            var rx = baseline.Rx.ToArray();
            var bad = Convert.FromHexString(rx[0]);
            bad[offset] ^= 0x40;
            rx[0] = Convert.ToHexString(bad);
            var transport = new Replay(rx);
            await RejectAsync(() => new StcUsbProtocol(transport).IdentifyAsync(default), "STC_USB_PROTOCOL");
            Check(transport.Writes.Count == 1, $"bad report framing byte {offset} rejected before erase");
        }
        foreach (var statusOffset in new[] { 20, 22 })
        {
            var transport = new Replay(ChangeStatus(baseline.Rx, statusOffset, 0x10));
            await RejectAsync(() => new StcUsbProtocol(transport).IdentifyAsync(default), "STC_USB_PROTOCOL");
            Check(transport.Writes.Count == 1, $"valid checksum but unsupported identity/ROM {statusOffset} refused");
        }
        foreach (var size in new[] { 0, 65025 })
        {
            var transport = new Replay(baseline.Rx);
            var protocol = new StcUsbProtocol(transport);
            await protocol.IdentifyAsync(default);
            await RejectAsync(() => protocol.ProgramAsync(new byte[size], settings, null, default), "STC_USB_PROTOCOL");
            Check(transport.Writes.Count == 1, $"image boundary {size} rejected before erase");
        }
        var invalidTrim = new Replay(ChangeStatus(baseline.Rx, 26, 0xff));
        var trimProtocol = new StcUsbProtocol(invalidTrim);
        await trimProtocol.IdentifyAsync(default);
        await RejectAsync(() => trimProtocol.ProgramAsync([1], settings with { ClockMode = StcClockMode.InternalRc, ClockFrequencyHz = 24000000 }, null, default), "STC_USB_PROTOCOL");
        Check(invalidTrim.Writes.Count == 1, "invalid per-chip calibration refused before erase");
        var pack = await new PackRepository(Path.Combine(output, "repository")).ImportAsync(Path.GetFullPath(archive));
        var device = pack.Manifest.Devices.Single(d => d.Id == "STC8H8K64U");
        Check(device.FlashBytes == 65024 && device.RamBytes == 8448, "code/EEPROM and USB RAM boundaries do not overlap");
        var capabilities = StcIspCapabilities.For(device);
        settings.ValidateFor(capabilities, requirePort: true);
        Check(capabilities.SupportsNativeUsb, "USB requires no saved COM port");
        await RejectAsync(() => Task.Run(() => settings.ValidateFor(StcIspCapabilities.For(pack.Manifest.Devices[0]))), "STC_USB_TARGET");
        await RejectAsync(() => Task.Run(() => (settings with { ClockMode = StcClockMode.InternalRc, ClockFrequencyHz = 12000000 }).ValidateFor(capabilities)), "STC_USB_CLOCK");
        var project = Path.Combine(output, "project");
        await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, "native_usb", project);
        var tools = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
        var backend = new Backend(baseline.Rx);
        var service = new StcIspService(tools, Path.Combine(output, "no-external-programmer"), Path.Combine(output, "user"), backend, TimeSpan.FromMilliseconds(500));
        await service.SaveSettingsAsync(project, settings);
        var build = await new BuildService(tools).BuildAsync(project);
        Check(build.Success, "real CMake + Ninja + SDCC build: " + build.Summary);
        var preview = await service.PreviewAsync(project, settings);
        var prepared = await service.PrepareAsync(project, settings);
        await service.VerifyPreparedAsync(prepared);
        Check(preview.Tool.Available && preview.Port == "USB-AUTO" && backend.Enumerations == 0 && backend.Opens == 0,
            "preview/prepare/verify work without stcgal, Python, device enumeration or device opens");
        Check(prepared.Image.Contains(Path.Combine("stc-usb", "latest"), StringComparison.Ordinal), "fixed latest snapshot directory");
        var original = await File.ReadAllBytesAsync(prepared.Image);
        await File.AppendAllTextAsync(prepared.Image, "\n");
        await RejectAsync(() => service.VerifyPreparedAsync(prepared), "STC_USB_PREPARE");
        await File.WriteAllBytesAsync(prepared.Image, original);
        var withHardware = settings with
        {
            ModifyHardware = true,
            Hardware = new StcHardwareOptions { PowerOnDelay = "short", WatchdogPrescale = "128", ResetPin = "false" }
        };
        await service.SaveSettingsAsync(project, withHardware);
        Check(await service.LoadSettingsAsync(project) == withHardware, "USB hardware options persist through actual project service");
        await RejectAsync(() => service.VerifyPreparedAsync(prepared), "STC_ISP_BUILD");
        Check(backend.Opens == 0 && backend.Enumerations == 0, "changed saved USB hardware options invalidate preparation before device discovery");
        var hardwarePrepared = await service.PrepareAsync(project, withHardware);
        await service.VerifyPreparedAsync(hardwarePrepared);
        Check(hardwarePrepared.Settings == withHardware, "new USB preparation binds full hardware configuration without a redundant compile");
        await service.SaveSettingsAsync(project, settings);
        await RejectAsync(() => service.VerifyPreparedAsync(hardwarePrepared), "STC_ISP_BUILD");
        prepared = await service.PrepareAsync(project, settings);
        backend.Multiple = true;
        await RejectAsync(async () => { await service.DownloadPreparedAsync(prepared); }, "STC_USB_MULTIPLE");
        Check(backend.Opens == 0, "multiple bootloaders never auto-select a board");
        backend.Multiple = false;
        var report = await service.DownloadPreparedAsync(prepared);
        Check(report.Success && report.ModelVerified && backend.Last!.Remaining == 0,
            "complete application service through real built HEX and injected USB ACKs");
        Check(backend.Last!.Writes.All(item => item.Length == 65), "service writes Windows report ID plus 64 payload bytes");
        backend.Absent = true;
        await RejectAsync(async () => { await service.DownloadPreparedAsync(prepared); }, "STC_USB_NOT_FOUND");
        Check(backend.Opens == 1, "absent bootloader waits then times out without opening another device");
        using (var cancel = new CancellationTokenSource(30))
        {
            try
            {
                await service.DownloadPreparedAsync(prepared, token: cancel.Token);
                throw new Exception("Cancellation ignored.");
            }
            catch (OperationCanceledException) { Check(backend.Opens == 1, "USB waiting can be cancelled without writes"); }
        }
        backend.Absent = false;
        // 此处仅注入模拟 HID；真实编译固件与录制应答长度可能不同，因此只检查擦除前的设置变化保护。
        backend.BeforeOpen = () => File.WriteAllText(Path.Combine(project, "src/main.c"), "void main(void) { while (1) {} }\n");
        await RejectAsync(async () => { await service.DownloadPreparedAsync(prepared); }, "STC_ISP_BUILD");
        Check(backend.Last!.Writes.Count == 1, "source change after discovery rejected after identity and before erase");
        await File.WriteAllLinesAsync(Path.Combine(output, "checks.txt"), checks);
        Console.WriteLine($"PASS {checks.Count} native USB checks; no real hardware opened.");
    }

    private static bool IsExit(byte[] report) => report.Length == 65 && report[6] == 0xff && report[4] == 0 && report[5] == 7;

    private static string[] ChangeStatus(string[] replies, int offset, byte value)
    {
        var result = replies.ToArray();
        var report = Convert.FromHexString(result[0]);
        report[6 + offset] = value;
        var size = BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(size), (ushort)report.AsSpan(3, size - 3).ToArray().Sum(x => x));
        result[0] = Convert.ToHexString(report);
        return result;
    }

    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try
        {
            await action();
        }
        catch (StudioXException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }

    private sealed class Replay(IEnumerable<string> replies) : IHidReportTransport, IDisposable
    {
        private readonly Queue<byte[]> replies = new(replies.Select(Convert.FromHexString));
        public List<byte[]> Writes { get; } = [];
        public int Remaining => replies.Count;
        public Task Write(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add(report.ToArray());
            return Task.CompletedTask;
        }
        public Task<byte[]> Read(int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(replies.Dequeue());
        }
        public void Dispose()
        {
        }
    }

    private sealed class Output : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }

    private sealed class Backend(string[] replies) : IStcUsbBackend
    {
        public int Enumerations
        {
            get; private set;
        }
        public int Opens
        {
            get; private set;
        }
        public bool Multiple
        {
            get; set;
        }
        public bool Absent
        {
            get; set;
        }
        public Action? BeforeOpen
        {
            get; set;
        }
        public Replay? Last
        {
            get; private set;
        }
        public IReadOnlyList<HidDevice> Enumerate()
        {
            Enumerations++;
            var device = new HidDevice("test-hid#vid_34bf&pid_1001#ephemeral", 0x34bf, 0x1001, "Offline fixture", 65, 65);
            return Absent ? [] : Multiple ? [device, device with { Path = device.Path + "-other" }] : [device];
        }
        public StcUsbConnection Open(HidDevice device, Action<string, byte[]> trace)
        {
            Opens++;
            BeforeOpen?.Invoke();
            Last = new(replies);
            return new(Last, Last);
        }
    }
}

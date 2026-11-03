using System.Buffers.Binary;
using System.Text.Json;
using StudioX.Ai8051Isp;
using StudioX.Engine;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;

/// <summary>官方编码器离线夹具交叉核对与未指定位保护；传输仅为内存队列。</summary>
internal static class NativeUsbOptionChecks
{
    private sealed record Rule(string Id, int Offset, byte Mask);

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var wire = JsonDocument.Parse(File.ReadAllText("tools/StudioX.StcIspValidation/fixtures/stc8h-usb.json"));
        var baseline = wire.RootElement.EnumerateArray().First(f => f.GetProperty("rc24MHz").GetBoolean());
        var replies = baseline.GetProperty("rx").EnumerateArray().Select(e => Convert.FromHexString(e.GetString()!)).ToArray();
        var image = Convert.FromHexString(baseline.GetProperty("binary").GetString()!);
        using var fixture = JsonDocument.Parse(File.ReadAllText("tools/StudioX.StcIspValidation/fixtures/stc8h-usb-options.json"));
        var settings = new StcIspSettings(DownloadMode: StcDownloadMode.NativeUsb, ModifyHardware: true,
            ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 24000000);
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString()!;
            var value = vector.GetProperty("value").GetString()!;
            var selected = settings with { Hardware = StcHardwareOptions.FromValues(new Dictionary<string, string> { [id] = value }) };
            var command = await ProgramAsync(replies, image, selected);
            check(Convert.ToHexString(command) == vector.GetProperty("command").GetString(),
                $"USB official PE serializer fixture: {id}={value}");
        }
        var rules = new Rule[]
        {
            new("reset_pin_enabled", 42, 0x10), new("clock_gain", 41, 0x02),
            new("watchdog_por_enabled", 43, 0x20), new("watchdog_stop_idle", 43, 0x08),
            new("watchdog_prescale", 43, 0x07), new("low_voltage_reset", 42, 0x40),
            new("low_voltage_threshold", 42, 0x03), new("eeprom_erase_enabled", 37, 0x02),
            new("bsl_pindetect_enabled", 37, 0x01), new("por_reset_delay", 41, 0x80)
        };
        var iterations = 0;
        foreach (var rule in rules)
        {
            foreach (var value in StcHardwareCatalog.For("STC8H8K64U").Single(f => f.Id == rule.Id).Values)
            {
                var hardware = StcHardwareOptions.FromValues(new Dictionary<string, string> { [rule.Id] = value.Value });
                var selected = settings with { ClockMode = StcClockMode.Preserve, ClockFrequencyHz = null, Hardware = hardware };
                for (var seed = 0; seed < 256; seed++)
                {
                    var current = ChangeStatus(replies, rule.Offset - 28, (byte)seed);
                    // 同时改变无关保留区，避免只在全 FF 样本上看似保留成功。
                    current = ChangeStatus(current, 8, (byte)(seed ^ 0xa5));
                    var original = current[0].AsSpan(7, 16).ToArray();
                    var command = await ProgramAsync(current, image, selected);
                    for (var index = 0; index < original.Length; index++)
                    {
                        var offset = index + 29;
                        var mask = offset == rule.Offset ? rule.Mask : 0;
                        if ((command[offset] & ~mask) != (original[index] & ~mask))
                        {
                            throw new InvalidOperationException($"USB changed an unspecified bit: {rule.Id}, seed {seed}, offset {offset}");
                        }
                    }
                    iterations++;
                }
            }
        }
        check(iterations == 7168, "USB all 256 raw-byte states preserve unselected bits, clock/trim and reserved bytes: 7168 cases");
        var disabled = settings with
        {
            ClockMode = StcClockMode.Preserve, ClockFrequencyHz = null, ModifyHardware = false,
            Hardware = new StcHardwareOptions { Uart1Remap = "true", ResetPin = "true", WatchdogPrescale = "2" }
        };
        var unchanged = await ProgramAsync(replies, image, disabled);
        check(unchanged.AsSpan(29, 16).SequenceEqual(replies[0].AsSpan(7, 16)), "USB disabled hardware modifications preserve every current option and inactive draft");
        foreach (var partition in new byte[] { 0, 1, 0x80, 0xfd, 0xff })
        {
            using var transport = new Replay(ChangeStatus(replies, 16, partition));
            var protocol = new StcUsbProtocol(transport);
            await protocol.IdentifyAsync(default);
            await RejectAsync(() => protocol.ProgramAsync(image, disabled, null, default), "STC_USB_PROTOCOL");
            check(transport.Writes.Count == 1, $"USB partition {partition:X2} rejected before setup/erase/write");
        }
        foreach (var hardware in new[] { new StcHardwareOptions { Uart1Remap = "true" }, new StcHardwareOptions { ResetOutputLevel = "low" },
            new StcHardwareOptions { WatchdogPrescale = "3" }, new StcHardwareOptions { LowVoltageThreshold = "4" } })
        {
            using var transport = new Replay(replies);
            var protocol = new StcUsbProtocol(transport);
            await protocol.IdentifyAsync(default);
            await RejectAsync(() => protocol.ProgramAsync(image, settings with { Hardware = hardware }, null, default),
                hardware.Uart1Remap is not null || hardware.ResetOutputLevel is not null ? "STC_USB_OPTIONS" : "STC_HARDWARE_OPTION");
            check(transport.Writes.Count == 1, "USB direct protocol call validates unsupported/invalid hardware settings before erase");
        }
        check(StcHardwareCatalog.For("STC8H8K64U").Count(f => StcUsbHardwareOptions.Supports("STC8H8K64U", f.Id)) == 10 &&
            !StcUsbHardwareOptions.Supports("STC8H1K08", "reset_pin_enabled") &&
            !StcUsbHardwareOptions.Supports("STC32G12K128", "reset_pin_enabled"), "USB capability remains exact target specific; no adjacent-model or MCS-251 promotion");
    }

    private static async Task<byte[]> ProgramAsync(byte[][] replies, byte[] image, StcIspSettings settings)
    {
        using var transport = new Replay(replies);
        var protocol = new StcUsbProtocol(transport);
        await protocol.IdentifyAsync(default);
        await protocol.ProgramAsync(image, settings, null, default);
        var report = transport.Writes[^2];
        if (report.Length != 65 || report[6] != 4 || BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(4)) != 51 ||
            BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(51)) != report.AsSpan(3, 48).ToArray().Sum(b => b))
        {
            throw new InvalidOperationException("Invalid USB options framing/checksum.");
        }
        return report.AsSpan(6, 45).ToArray();
    }

    private static byte[][] ChangeStatus(byte[][] replies, int offset, byte value)
    {
        var result = replies.Select(r => r.ToArray()).ToArray();
        result[0][6 + offset] = value;
        var size = BinaryPrimitives.ReadUInt16BigEndian(result[0].AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(result[0].AsSpan(size), (ushort)result[0].AsSpan(3, size - 3).ToArray().Sum(b => b));
        return result;
    }

    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (StudioXException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected USB rejection: " + code);
    }

    private sealed class Replay(byte[][] replies) : IHidReportTransport, IDisposable
    {
        private readonly Queue<byte[]> pending = new(replies);
        public List<byte[]> Writes { get; } = [];
        public Task Write(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add(report.ToArray());
            return Task.CompletedTask;
        }
        public Task<byte[]> Read(int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(pending.Dequeue());
        }
        public void Dispose()
        {
        }
    }
}

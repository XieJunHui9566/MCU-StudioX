using System.Buffers.Binary;
using System.Text.Json.Nodes;
using StudioX.Ai8051Isp;
using StudioX.Application;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class DownloadChecks
{
    private sealed class Backend : IMx251IspBackend, IHidReportTransport, IDisposable
    {
        public static readonly HidDevice Device = new(@"\\?\hid#vid_34bf&pid_ff0f&mi_00#offline", 0x34bf, 0xff0f, "STC USB Serial（离线夹具）", 65, 65);
        public IReadOnlyList<HidDevice> Devices = [Device];
        public List<byte[]> Writes = [];
        public int Opens, Closes, WrittenBlocks;
        public string Fault = "";
        private Action<string, byte[]> trace = null!;
        private byte[] pending = [], options = [];
        private bool configured;
        public IReadOnlyList<HidDevice> Enumerate() => Devices;
        public Mx251IspConnection Open(HidDevice device, Action<string, byte[]> capture)
        {
            Opens++;
            Writes.Clear();
            WrittenBlocks = 0;
            configured = false;
            trace = capture;
            return new(this, this);
        }
        public void Dispose() => Closes++;
        public Task Write(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (report.Length != 65 || (report.Sum(x => x) & 255) != 0)
            {
                throw new Exception("bad outgoing framing");
            }
            Writes.Add(report.ToArray());
            pending = report;
            trace("tx", report);
            return Task.CompletedTask;
        }
        public Task<byte[]> Read(int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] payload;
            switch (pending[2])
            {
                case 0:
                    payload = Convert.FromHexString("510104FF000000000000000000000071000000");
                    break;
                case 0x41:
                    payload = Convert.FromHexString("50026295608030012FFFBF3E3F26EDF7FC74550078B41183BD32561D201C190100010004A0260510800E89C2F94D5F7DA7B1F8FE0000");
                    if (Fault == "target")
                    {
                        payload[21] ^= 1;
                    }
                    if (Fault == "options")
                    {
                        payload[10] ^= 1;
                    }
                    if (configured)
                    {
                        options.AsSpan(29, 7).CopyTo(payload.AsSpan(1));
                        if (Fault == "readback")
                        {
                            payload[5] ^= 1;
                        }
                        if (Fault == "readback-hz")
                        {
                            payload[1] ^= 1;
                        }
                        if (Fault == "readback-range")
                        {
                            payload[6] ^= 0x10;
                        }
                        if (Fault == "readback-divider")
                        {
                            payload[7] ^= 1;
                        }
                    }
                    break;
                case 0x43:
                    if (pending[8] == 0)
                    {
                        int count = pending[9];
                        payload = new byte[2 + count * 2];
                        payload[1] = (byte)count;
                        for (int i = 0; i < count; i++)
                        {
                            // 独立模拟四个不连续频段（计数单位2400Hz），覆盖低频分频与频段间隙。
                            int[] ends = [1800, 3300, 3000, 5500, 8400, 14500, 12000, 23000];
                            int band = count == 8 ? i / 2 : pending[11 + i * 2] >> 4;
                            int value = count == 8 ? ends[i] : (int)Math.Round(ends[band * 2] + pending[10 + i * 2] * (ends[band * 2 + 1] - ends[band * 2]) / 255.0);
                            if (Fault == "coarse")
                            {
                                value = 20000;
                            }
                            if (Fault == "unreachable")
                            {
                                value = i % 2 == 0 ? 25000 : 26000;
                            }
                            if (Fault == "fine-error" && count != 8)
                            {
                                value += 6000;
                            }
                            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + i * 2), (ushort)value);
                        }
                    }
                    else if (pending[8] == 3)
                    {
                        payload = [3, 0x78, 0xb4, 0, 0, 0, 0, 0];
                    }
                    else if (pending[8] == 4)
                    {
                        if (WrittenBlocks == 0)
                        {
                            throw new Exception("options without code");
                        }
                        configured = true;
                        options = pending.AsSpan(8, pending[7]).ToArray();
                        // 独立历史抓包常量：只允许当次 IRC 校准7字节变化，32位及其它选项不能漂移。
                        var golden = Convert.FromHexString("0400005AA5FFFFFF00FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00FF02629560803001FFFFFF3E3FBFAFF7FCFE");
                        options.AsSpan(29, 7).CopyTo(golden.AsSpan(29));
                        if (!golden.SequenceEqual(options))
                        {
                            throw new Exception("fixed 32-bit packet differs from capture");
                        }
                        payload = Fault == "config-ack" ? [4, 0] : [4, 0x54];
                    }
                    else
                    {
                        payload = [pending[8]];
                    }
                    break;
                case 0x45:
                    if (Fault == "timeout")
                    {
                        throw new TimeoutException("injected block timeout");
                    }
                    WrittenBlocks++;
                    payload = Fault == "block-ack" ? [2, 0] : [2, 0x54];
                    break;
                default:
                    throw new Exception("unexpected request " + Convert.ToHexString(pending));
            }
            var result = new byte[65];
            result[1] = 0xa5;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)payload.Length);
            payload.CopyTo(result, 4);
            result[64] = unchecked((byte)-result.Take(64).Sum(x => x));
            trace("rx", result);
            return Task.FromResult(result);
        }
    }
    internal static async Task RunAsync(string packArchive, string toolsRoot, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use new output directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        async Task Reject(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (Exception e) when (e is StudioXException or InvalidDataException or TimeoutException or OperationCanceledException or IOException)
            {
                Check(true, name);
                return;
            }
            throw new Exception("Expected rejection: " + name);
        }
        var catalog = new ToolsetCatalog(toolsRoot);
        var builder = new BuildService(catalog);
        var backend = new Backend();
        var isp = new Mx251IspService(catalog, Path.Combine(output, "data"), backend);
        await using var devices = new DeviceHub();
        var downloads = new Mx251DownloadService(isp, builder, devices);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(packArchive);
        var root = Path.Combine(output, "project");
        var project = await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "download", root);
        Check(ProjectCapabilities.For(project).Download && !ProjectCapabilities.For(project).Debug, "MX download enabled; debug remains unavailable");
        var automatic = await downloads.ResolveDeviceAsync(root);
        Check(automatic.Selected?.DevicePath == Backend.Device.Path && automatic.Selected.CpuMode == 32, "first click selects sole bridge without saved configuration");
        var second = Backend.Device with
        {
            Path = Backend.Device.Path + "-other"
        };
        backend.Devices = [Backend.Device, second];
        Check((await downloads.ResolveDeviceAsync(root)).Selected is null, "multiple unsaved devices request inline selection");
        Check((await downloads.ResolveDeviceAsync(root, second.Path)).Selected?.DevicePath == second.Path, "explicit unsaved UI selection used directly");
        backend.Devices = [];
        await Reject(() => downloads.ResolveDeviceAsync(root), "no device gives local actionable error");
        backend.Devices = [Backend.Device];
        Check(backend.Opens == 0, "device resolution never opens programming transport");
        await Reject(() => downloads.SaveSettingsAsync(root, new(Backend.Device.Path, 8)), "8-bit configuration rejected");
        foreach (int invalidHz in new[] { -1, 0, 999999, 48000001, int.MaxValue })
        {
            await Reject(() => downloads.SaveSettingsAsync(root, new(Backend.Device.Path, 32, invalidHz)), $"invalid frequency {invalidHz} rejected");
        }
        backend.Devices = [];
        await downloads.SaveSettingsAsync(root, new(IrcHz: 11059200));
        Check((await downloads.LoadSettingsAsync(root)).IrcHz == 11059200, "frequency saved while disconnected without touching hardware");
        backend.Devices = [Backend.Device, second];
        Check((await downloads.ResolveDeviceAsync(root)).SavedSettings.IrcHz == 11059200, "inline device choice retains saved frequency");
        Check((await downloads.ResolveDeviceAsync(root, second.Path)).Selected?.IrcHz == 11059200, "explicit device resolution retains frequency");
        backend.Devices = [Backend.Device];
        await downloads.SaveSettingsAsync(root, new(Backend.Device.Path));
        backend.Devices = [Backend.Device, second];
        Check((await downloads.ResolveDeviceAsync(root)).Selected?.DevicePath == Backend.Device.Path, "saved connected bridge used even with multiple devices");
        backend.Devices = [second];
        Check((await downloads.ResolveDeviceAsync(root)).Selected is null, "missing saved bridge is not silently replaced");
        backend.Devices = [Backend.Device];
        await Reject(() => downloads.PrepareAsync(root), "no build rejected before opening hardware");
        Check(backend.Opens == 0, "settings/list/prepare do not open protocol");
        foreach (var model in new[] { "small", "large" })
        {
            var settings = new Mx251ProjectSettingsService(builder, new ProjectFileService());
            await settings.SaveModelAsync(root, await settings.ReadAsync(root), model);
            var built = await builder.BuildAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, model + ".log"), built.Log);
            Check(built.Success, model + " real compile");
            var prepared = await downloads.PrepareAsync(root);
            var report = await downloads.DownloadPreparedAsync(prepared);
            Check(report.Success && report.Result.ProgramAcknowledged && report.Result.ConfigurationReadback &&
                !report.Result.FlashReadback && !report.Result.RuntimeVerified, model + " full offline download and mandatory configuration readback");
            Check(backend.Writes.Count(w => w[2] == 0x43 && w[8] == 4) == 1 && backend.Writes.Count(w => w[2] == 0x41) == 2, model + " actual mode packet sent once, detected twice");
            Check(backend.Opens == backend.Closes, model + " ownership released");
            await JsonStore.WriteAsync(Path.Combine(output, model + "-download.json"), report);
        }
        foreach (var (hz, divider) in new[] { (1000000, 5), (6000000, 1), (11059200, 1), (16000000, 2), (24000000, 1), (40000000, 1), (48000000, 1), (4320000, 1), (7920000, 1) })
        {
            await downloads.SaveSettingsAsync(root, new(Backend.Device.Path, IrcHz: hz));
            int openedBeforeClock = backend.Opens;
            await Reject(() => downloads.PrepareAsync(root), "clock change invalidates old firmware " + hz);
            Check(backend.Opens == openedBeforeClock, "stale clock refused before hardware " + hz);
            Check((await builder.BuildAsync(root)).Success, "rebuild for changed clock " + hz);
            var preparedClock = await downloads.PrepareAsync(root);
            var result = await downloads.DownloadPreparedAsync(preparedClock);
            byte[] wire = backend.Writes.Single(w => w[2] == 0x43 && w[8] == 4);
            uint measured = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(37));
            Check(result.Success && result.Result.ConfigurationReadback && preparedClock.Settings.IrcHz == hz &&
                Math.Abs((double)measured - hz) / hz <= 0.005 && wire[43] == divider && result.Result.Target.IrcHz == measured,
                $"{hz} Hz target propagated to wire and readback; divider {divider}");
            if (hz is 4320000 or 7920000)
            {
                var fine = backend.Writes.Single(w => w[2] == 0x43 && w[8] == 0 && w[9] == 12);
                Check(hz == 4320000 ? fine[10] == 0 : fine[32] == 255, $"{hz} Hz trim endpoint preserved without byte wrap");
            }
            await JsonStore.WriteAsync(Path.Combine(output, $"clock-{hz}.json"), result);
        }
        await downloads.SaveSettingsAsync(root, new(Backend.Device.Path));
        Check((await builder.BuildAsync(root)).Success, "rebuild 40MHz before prepared-clock mutation");
        var clockSnapshot = await downloads.PrepareAsync(root);
        await downloads.SaveSettingsAsync(root, new(Backend.Device.Path, IrcHz: 24000000));
        int beforeClockChange = backend.Opens;
        await Reject(() => downloads.DownloadPreparedAsync(clockSnapshot), "changed frequency invalidates prepared download");
        Check(backend.Opens == beforeClockChange, "stale clock rejected before opening hardware");
        await downloads.SaveSettingsAsync(root, new(Backend.Device.Path));
        Check((await builder.BuildAsync(root)).Success, "rebuild 40MHz before protocol fault cases");
        foreach (var fault in new[] { "target", "options", "coarse", "unreachable", "fine-error", "block-ack", "timeout", "config-ack", "readback", "readback-hz", "readback-range", "readback-divider" })
        {
            backend.Fault = fault;
            var prepared = await downloads.PrepareAsync(root);
            await Reject(() => downloads.DownloadPreparedAsync(prepared), fault + " stops with original diagnostic");
            if (fault is "target" or "options" or "coarse" or "unreachable" or "fine-error")
            {
                Check(!backend.Writes.Any(w => w[2] == 0x43 && w[8] == 3), fault + " no erase");
            }
            if (fault is "block-ack" or "timeout")
            {
                Check(!backend.Writes.Any(w => w[2] == 0x43 && w[8] == 4 || w[2] == 0xff), fault + " no options, run or write retry");
            }
            Check(!File.Exists(Path.Combine(Path.GetDirectoryName(prepared.LogPath)!, "result.json")) && backend.Opens == backend.Closes, fault + " no success record, lease released");
        }
        backend.Fault = "";
        var stale = await downloads.PrepareAsync(root);
        var source = Path.Combine(root, "src/main.c");
        var text = await File.ReadAllTextAsync(source);
        await File.WriteAllTextAsync(source, text + "\n// changed\n");
        int opens = backend.Opens;
        await Reject(() => downloads.DownloadPreparedAsync(stale), "stale source rejected");
        await File.WriteAllTextAsync(source, text);
        await File.AppendAllTextAsync(stale.ImagePath, "\n");
        await Reject(() => downloads.DownloadPreparedAsync(stale), "mutated snapshot rejected");
        Check(backend.Opens == opens, "both mutations rejected before open");
        var selected = await downloads.PrepareAsync(root);
        backend.Devices = [Backend.Device with { Path = Backend.Device.Path + "-other" }];
        await Reject(() => downloads.DownloadPreparedAsync(selected), "missing selection never falls back to another bridge");
        backend.Devices = [Backend.Device, Backend.Device with { Path = Backend.Device.Path + "-other" }];
        Check((await downloads.DownloadPreparedAsync(await downloads.PrepareAsync(root))).Success, "multiple bridges honor exact selection");
        using (await devices.ReserveAsync("hid:" + Backend.Device.Path))
        {
            await Reject(async () => await downloads.DownloadPreparedAsync(await downloads.PrepareAsync(root)), "application device ownership conflict");
        }
        using (ProbeLease.Acquire())
        {
            await Reject(async () => await downloads.DownloadPreparedAsync(await downloads.PrepareAsync(root)), "process-wide probe ownership conflict");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var prepared = await downloads.PrepareAsync(root);
            opens = backend.Opens;
            canceled.Cancel();
            await Reject(() => downloads.DownloadPreparedAsync(prepared, token: canceled.Token), "canceled operation does not open hardware");
            Check(opens == backend.Opens, "cancellation open count unchanged");
        }
        var configPath = Path.Combine(root, "mx251.json");
        var configuration = await File.ReadAllTextAsync(configPath);
        var invalid = JsonNode.Parse(configuration)!;
        invalid["target"] = "AI8051U-34K64-8";
        await File.WriteAllTextAsync(configPath, invalid.ToJsonString());
        await Reject(() => downloads.PrepareAsync(root), "8-bit project target rejected");
        await File.WriteAllTextAsync(configPath, configuration);
        Check((await new BuildMemoryService(catalog).ReadAsync(root)).Targets.Count == 1, "download snapshots do not invalidate build statistics");
        Check(Directory.GetDirectories(Path.Combine(root, ".build/mx251-isp")).Select(Path.GetFileName).SequenceEqual(new[] { "latest" }), "download cache retains only latest");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            checks,
            hardwareOpened = false
        });
    }
}

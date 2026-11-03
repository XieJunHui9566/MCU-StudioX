using System.Runtime.CompilerServices;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class LaunchChecks
{
    public static async Task<int> RunAsync(string archive, string runtime, string output)
    {
        if (Directory.Exists(output))
        {
            throw new InvalidOperationException("Use a new validation directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Reject(Func<Task> action, string code, string name)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Accepted: " + name);
            }
            catch (StudioXException error) when (error.Code == code) { checks.Add(name); }
        }
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var pack = await packs.ImportAsync(archive);
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builds = new BuildService(catalog);
        foreach (var target in StcMon51Targets.DeviceIds)
        {
            var project = Path.Combine(output, target);
            await new ProjectService().CreateAsync(pack, target, "bare", "LaunchProbe", project);
            await new StcBuildWorkflowService(builds, () => false).PrepareDebugAsync(project);
            await using var hub = new DeviceHub();
            var silent = new PortFixture("COM18", new()
            {
                Silent = true
            });
            var good = new PortFixture("COM18", new()
            {
                AllowUserProgramDownload = true,
                UseMemoryImage = true
            });
            var readFailure = new PortFixture("COM18", new()) { DropRegisters = true };
            var badBuild = new PortFixture("COM18", new());
            PortFixture current = silent;
            var connections = new List<SerialSettings>();
            await using var debug = new DebugSessionService(Path.Combine(output, target + "-data"), hub, settings =>
            {
                connections.Add(settings);
                return current;
            });
            await debug.OpenProjectAsync(project);
            var isp = new StcIspService(catalog, runtime, Path.Combine(output, target + "-isp"));
            var realSetup = new Mon51SetupService(isp, hub, packs, () => debug.IsActive);
            var setup = new SetupFixture(realSetup, isp, hub);
            var launch = new Mon51LaunchService(debug, setup);
            var log = new List<string>();
            var progress = new InlineProgress(log.Add);
            var buildsRequested = 0;
            async Task<Mon51DownloadPreparation> BuildAsync(CancellationToken token)
            {
                buildsRequested++;
                Check(good.Inner.Commands.Any(command => command.SequenceEqual(new byte[] { 0, 0 })) && !good.Inner.Commands.Any(command => command[0] == 6), target + " handshake completes before compilation and erase");
                var build = await builds.BuildAsync(project, cancellationToken: token);
                Check(build.Success, target + " real SDCC Mon51 build succeeds: " + build.Summary);
                return await debug.PrepareMon51DownloadAsync(token);
            }
            var result = await launch.StartAsync(project, BuildAsync, progress);
            Check(result == Mon51LaunchResult.PortRequired && connections.Count == 0 && setup.Prepares == 0 && buildsRequested == 0,
                target + " missing download COM returns navigation result before transport, compilation or monitor preparation");
            var settings = new StcIspSettings(Port: "COM18", TransferBaud: target == StcMon51Targets.HighVoltageDevice ? 57600 : 38400);
            await JsonStore.WriteAsync(PathBoundary.Resolve(project, StcIspSettings.RelativePath), settings);
            var presentationCalls = 0;
            result = await launch.StartAsync(project, BuildAsync, progress, installMonitor: (prepared, cancel) =>
            {
                presentationCalls++;
                return setup.ExecuteAsync(prepared, progress, cancel);
            });
            Check(result == Mon51LaunchResult.MonitorInstalled && setup.Prepares == 1 && setup.Executions == 1 && buildsRequested == 0,
                target + " failed handshake installs exact monitor without building or updating user firmware");
            Check(presentationCalls == 1, target + " automatic monitor installation runs through the caller's shared ISP output operation");
            Check(setup.Prepared!.ExpectedModel == target && setup.Prepared.ImageSha256 == StcMon51Targets.SetupSha256(target) &&
                setup.Prepared.Port == settings.Port && setup.Prepared.Settings.TransferBaud == settings.TransferBaud && connections.Single().BaudRate == settings.TransferBaud,
                target + " handshake and ISP use saved COM/baud and the exact F/L monitor hash");
            Check(setup.Prepared.Image == PathBoundary.Resolve(project, ".build/stc-monitor/latest/monitor.bin"),
                target + " monitor preparation reuses fixed latest output instead of creating hash directories");
            using (ProjectBuildStorage.Acquire(project, "stc-isp"))
            {
                await Reject(() => realSetup.PrepareForProjectAsync(project, settings), "PROJECT_OUTPUT_BUSY",
                    target + " monitor preparation cannot replace output while an ISP artifact lease is held");
            }
            Check(silent.Closed && !debug.IsActive && debug.MonitorSession is null && debug.State == DebugState.Disconnected && connections.Count == 1,
                target + " serial ownership is released before setup and no automatic post-setup reconnect occurs");
            var before = log.FindIndex(line => line.Contains("第一次重新上电"));
            var after = log.FindIndex(line => line.Contains("第二次重新上电"));
            Check(before >= 0 && after > before && log[before].Contains("ISP") && log[after].Contains("再次点击调试") && log[after].Contains("本次调试已退出"),
                target + " separate ordered power-cycle prompts explain ISP entry and firmware activation");
            log.Clear();
            current = good;
            result = await launch.StartAsync(project, BuildAsync, progress);
            Check(result == Mon51LaunchResult.Started && buildsRequested == 1 && setup.Executions == 1 && debug.MonitorSession is { HasSymbols: true, Pc: 0xdbfd } && debug.State == DebugState.Stopped,
                target + " next user click builds, downloads, reads back and opens source debugging on the same connection");
            Check(!log.Any(line => line.Contains("重新上电")) && !good.Inner.Running,
                target + " existing monitor needs no power cycle or confirmation and remains paused");
            await debug.StopAsync();
            current = readFailure;
            await Reject(() => launch.StartAsync(project, BuildAsync, progress), "MON51_TIMEOUT", target + " register read timeout after successful hello never installs a monitor");
            Check(setup.Executions == 1 && readFailure.Closed && !debug.IsActive,
                target + " post-handshake read failure releases connection without ISP fallback");
            current = badBuild;
            await Reject(() => launch.StartAsync(project, _ => throw new StudioXException("TEST_BUILD", "compiler failed"), progress), "TEST_BUILD", target + " compilation failure is preserved without monitor fallback");
            Check(setup.Executions == 1 && badBuild.Closed && !badBuild.Inner.Commands.Any(command => command[0] is 6 or 8),
                target + " compile failure neither erases nor resumes target");
            current = new("COM18", new());
            using (await hub.ReserveAsync("serial:COM18"))
            {
                await Reject(() => launch.StartAsync(project, BuildAsync, progress), "DEVICE_OWNED", target + " busy serial owner is preserved without monitor installation");
            }
            current = new("COM18", new()
            {
                Silent = true
            });
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try
                {
                    await launch.StartAsync(project, BuildAsync, progress, cancel.Token);
                    throw new InvalidOperationException("Cancellation accepted");
                }
                catch (OperationCanceledException) { checks.Add(target + " cancelled handshake does not become an ISP fallback"); }
            }
            Check(setup.Executions == 1 && current.Closed && !debug.IsActive, target + " cancellation releases ownership without setup");
            current = new("COM18", new()
            {
                Silent = true
            })
            {
                FailClose = true
            };
            log.Clear();
            try
            {
                await launch.StartAsync(project, BuildAsync, progress);
                throw new InvalidOperationException("Close failure accepted");
            }
            catch (IOException error) when (error.Message == "fixture close failed")
            {
                checks.Add(target + " failed serial release is preserved instead of becoming monitor installation");
            }
            Check(setup.Executions == 1 && !log.Any(line => line.Contains("第一次重新上电")) && !debug.IsActive &&
                File.ReadAllText(debug.SessionLogPath!).Contains("TIMEOUT"),
                target + " release failure keeps the raw handshake timeout log and never enters ISP");
            current = new("COM18", new()
            {
                Silent = true
            });
            setup.FailExecution = true;
            log.Clear();
            await Reject(() => launch.StartAsync(project, BuildAsync, progress), "MON51_SETUP_FAILED", target + " failed monitor write is never reported as installed");
            Check(log.Count(line => line.Contains("第一次重新上电")) == 1 && !log.Any(line => line.Contains("第二次重新上电")) && !debug.IsActive,
                target + " failed setup gives only ISP prompt and never promises activation or reconnects");
            Check(Directory.GetDirectories(PathBoundary.Resolve(project, ".build/stc-monitor")).Select(Path.GetFileName).SequenceEqual(new[] { "latest" }),
                target + " repeated monitor preparations retain one latest directory");
            await debug.StopAsync();
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            hardware = false,
            serialPortsOpened = 0,
            count = checks.Count,
            checks
        });
        Console.WriteLine($"PASS {checks.Count} Mon51 launch checks; protocol fixtures and real offline SDCC builds only.");
        return 0;
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class SetupFixture(Mon51SetupService real, StcIspService isp, DeviceHub hub) : IMon51SetupService
    {
        public int Prepares
        {
            get; private set;
        }
        public int Executions
        {
            get; private set;
        }
        public bool FailExecution
        {
            get; set;
        }
        public StcIspPreparation? Prepared
        {
            get; private set;
        }
        public async Task<StcIspPreparation> PrepareForProjectAsync(string project, StcIspSettings settings, CancellationToken token = default)
        {
            Prepares++;
            Prepared = await real.PrepareForProjectAsync(project, settings, token);
            await isp.VerifyPreparedAsync(Prepared, token);
            return Prepared;
        }
        public async Task<StcIspReport> ExecuteAsync(StcIspPreparation prepared, IProgress<string>? output, CancellationToken token = default)
        {
            // 此租约断言验证握手连接已释放；夹具只返回制作应答，不启动 Python 或打开串口。
            using var reservation = await hub.ReserveAsync("serial:" + prepared.Port, token);
            token.ThrowIfCancellationRequested();
            Executions++;
            return new(!FailExecution, FailExecution ? "fixture write failed" : "STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", prepared.LogPath, FailExecution ? 1 : 0, false, true);
        }
    }

    private sealed class PortFixture(string port, Mon51OfflineTransport inner) : IDeviceTransport
    {
        public Mon51OfflineTransport Inner => inner;
        public string Key => "serial:" + port;
        public bool IsSimulated => true;
        public bool Closed
        {
            get; private set;
        }
        public bool DropRegisters
        {
            get; init;
        }
        public bool FailClose
        {
            get; init;
        }
        public ValueTask OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var bytes in inner.ReceiveAsync(cancellationToken))
            {
                yield return bytes;
            }
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (DropRegisters && bytes.Length > 4 && bytes.Span[3] == 0 && bytes.Span[4] == 1)
            {
                return ValueTask.CompletedTask;
            }
            return inner.SendAsync(bytes, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            Closed = true;
            await inner.DisposeAsync();
            if (FailClose)
            {
                throw new IOException("fixture close failed");
            }
        }
    }
}

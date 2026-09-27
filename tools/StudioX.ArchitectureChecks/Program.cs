using StudioX.Application.Simulation;
using StudioX.Desktop;
using StudioX.Devices;
using StudioX.Foundation;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException(description);
    }
    Console.WriteLine("PASS " + description);
    checks++;
}

var order = new List<string>();
var agentFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var coordinator = new ProjectTransitionCoordinator(
    async () => { order.Add("agent"); await agentFinished.Task; },
    () => { order.Add("breakpoints"); return Task.CompletedTask; },
    _ => { order.Add("preview"); return Task.CompletedTask; },
    _ => { order.Add("debugger"); return Task.CompletedTask; },
    _ => { order.Add("editor"); return Task.CompletedTask; });
var transition = coordinator.StopCurrentAsync(CancellationToken.None);
Check(!transition.IsCompleted && order.SequenceEqual(["agent"]), "project transition waits for the previous Agent call before releasing project resources");
agentFinished.SetResult();
await transition;
Check(order.SequenceEqual(["agent", "breakpoints", "preview", "debugger", "editor"]), "open and close share an ordered transition; editor anchors are released after debugger navigation");

order.Clear();
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await coordinator.StopCurrentAsync(cancelled.Token);
    throw new InvalidOperationException("Cancelled transition ran.");
}
catch (OperationCanceledException)
{
    Check(order.Count == 0, "cancelled project transition keeps the current project untouched");
}
var failure = new InvalidOperationException("original preview failure");
var failingCoordinator = new ProjectTransitionCoordinator(
    () => Task.CompletedTask,
    () => Task.CompletedTask,
    _ => Task.FromException(failure),
    _ => { order.Add("debugger"); return Task.CompletedTask; },
    _ => { order.Add("editor"); return Task.CompletedTask; });
try
{
    await failingCoordinator.StopCurrentAsync(CancellationToken.None);
    throw new InvalidOperationException("Transition failure swallowed.");
}
catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
{
    Check(order.Count == 0, "original transition errors propagate without clearing the editor or binding another project");
}

var root = Path.Combine(Path.GetTempPath(), "studiox-architecture-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await using var devices = new DeviceHub();
    await using var lab = new SimulationLabService(devices);
    var logReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var plotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var capturedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var captureSamples = 0;
    lab.LogReceived += frame =>
    {
        if (lab.LogFrames >= 3)
        {
            logReady.TrySetResult();
        }
        if (lab.IsRecording && Interlocked.Increment(ref captureSamples) >= 4)
        {
            capturedReady.TrySetResult();
        }
        CheckFrame(frame.Generation <= lab.Generation && frame.Text.Contains(','));
    };
    lab.PlotReceived += frame =>
    {
        if (lab.PlotFrames >= 3)
        {
            plotReady.TrySetResult();
        }
        CheckFrame(frame.Generation <= lab.Generation && double.IsFinite(frame.Value) && frame.State is 0 or 1);
    };
    var diagnostics = new List<string>();
    lab.Diagnostic += diagnostics.Add;
    await lab.StartAsync();
    var firstGeneration = lab.Generation;
    await Task.WhenAll(logReady.Task, plotReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
    Check(lab.IsConnected && lab.LogFrames >= 3 && lab.PlotFrames >= 3, "Application owns a single simulated session and two independent observers");
    await lab.StartAsync();
    Check(lab.Generation == firstGeneration, "repeated simulation start reuses the owned connection");
    try
    {
        await devices.OpenAsync(new SimulationTransport());
        throw new InvalidOperationException("Second device owner accepted.");
    }
    catch (StudioXException ex) when (ex.Code == "DEVICE_OWNED")
    {
        Check(true, "other callers cannot claim the same simulated device connection");
    }

    var capture = Path.Combine(root, "sample.sxcapture");
    await lab.StartCaptureAsync(capture);
    await capturedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await lab.StopAsync();
    var frames = new List<CapturedFrame>();
    await foreach (var frame in CaptureFile.ReadAsync(capture))
    {
        frames.Add(frame);
    }
    Check(frames.Count >= 3 && frames.Zip(frames.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence), "stop flushes a valid ordered capture before releasing the connection");
    Check(!lab.IsRecording && !lab.IsConnected && lab.Generation > firstGeneration && diagnostics.Any(value => value.Contains("丢帧")), "stop invalidates old UI callbacks and reports capture overflow counters");
    await lab.StartAsync();
    Check(lab.IsConnected && lab.Generation > firstGeneration, "released simulation connection can restart");
    try
    {
        await lab.StartCaptureAsync(capture);
        throw new InvalidOperationException("Capture overwritten.");
    }
    catch (StudioXException ex) when (ex.Code == "CAPTURE_EXISTS")
    {
        Check(!lab.IsRecording, "existing captures are protected and no failed recorder remains active");
    }
    await lab.StopAsync();
    await lab.StartAsync();
    var observerError = new InvalidOperationException("original failing diagnostic observer");
    lab.Diagnostic += _ => throw observerError;
    await lab.StartCaptureAsync(Path.Combine(root, "failing-observer.sxcapture"));
    try
    {
        await lab.StopAsync();
        throw new InvalidOperationException("Observer failure swallowed.");
    }
    catch (AggregateException ex) when (ex.Flatten().InnerExceptions.Contains(observerError))
    {
        Check(!lab.IsConnected && !lab.IsRecording, "diagnostic observer failure preserves the original error and still releases recorder, subscriptions and session");
    }
    await using (var nextOwner = await devices.OpenAsync(new SimulationTransport()))
    {
        Check(true, "failed cleanup notification cannot leak the single device owner");
    }
    Console.WriteLine($"PASS {checks} architecture checks; no hardware connected.");
}
finally
{
    // 只移除本次新建的临时夹具；不触碰用户工程、工具链或历史采集文件。
    var full = Path.GetFullPath(root);
    if (Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
        Path.GetFileName(full).StartsWith("studiox-architecture-checks-", StringComparison.Ordinal))
    {
        Directory.Delete(full, recursive: true);
    }
}

void CheckFrame(bool valid)
{
    if (!valid)
    {
        throw new InvalidOperationException("Simulation event contained invalid data.");
    }
}

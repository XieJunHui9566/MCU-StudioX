namespace StudioX.Application.Simulation;

using System.Globalization;
using System.Text;
using StudioX.Devices;
using StudioX.Foundation;

/// <summary>拥有模拟连接、日志/绘图订阅和采集记录；界面只观察数据，不持有设备会话。</summary>
public sealed class SimulationLabService(DeviceHub devices) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeviceSession? session;
    private CancellationTokenSource? consumersCancellation;
    private FrameSubscription? logSubscription;
    private FrameSubscription? plotSubscription;
    private Task[] consumers = [];
    private CancellationTokenSource? captureCancellation;
    private FrameSubscription? captureSubscription;
    private Task? captureTask;
    private long generation;
    private long logFrames;
    private long plotFrames;
    private bool disposed;

    public event Action<SimulationLogFrame>? LogReceived;
    public event Action<SimulationPlotFrame>? PlotReceived;
    public event Action<string>? Diagnostic;

    public bool IsConnected => session is not null;
    public bool IsRecording => captureTask is not null;
    public long Generation => Interlocked.Read(ref generation);
    public long LogFrames => Interlocked.Read(ref logFrames);
    public long PlotFrames => Interlocked.Read(ref plotFrames);

    public async Task StartAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is not null)
            {
                return;
            }

            session = await devices.OpenAsync(new SimulationTransport(), token).ConfigureAwait(false);
            var connectionGeneration = Interlocked.Increment(ref generation);
            Interlocked.Exchange(ref logFrames, 0);
            Interlocked.Exchange(ref plotFrames, 0);
            consumersCancellation = new CancellationTokenSource();
            // 每个观察者独立计数和缓冲；慢日志窗口不能阻塞绘图或记录器。
            logSubscription = session.Subscribe(128);
            plotSubscription = session.Subscribe(128);
            consumers =
            [
                ConsumeLogAsync(logSubscription, connectionGeneration, consumersCancellation.Token),
                ConsumePlotAsync(plotSubscription, connectionGeneration, consumersCancellation.Token)
            ];
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartCaptureAsync(string path, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is null)
            {
                throw new StudioXException("SIMULATION_REQUIRED", "请先连接模拟设备，再开始记录。");
            }
            if (captureTask is not null)
            {
                throw new StudioXException("CAPTURE_ACTIVE", "已有模拟记录正在进行。");
            }
            if (File.Exists(path))
            {
                throw new StudioXException("CAPTURE_EXISTS", "记录器不覆盖已有文件，请使用新文件名。");
            }

            captureCancellation = new CancellationTokenSource();
            captureSubscription = session.Subscribe(1024);
            // CaptureFile 使用 CreateNew；检查后另一个进程创建同名文件也不会被覆盖。
            captureTask = CaptureFile.WriteAsync(path, session, captureSubscription, captureCancellation.Token);
            if (captureTask.IsCompleted)
            {
                try
                {
                    await captureTask.ConfigureAwait(false);
                }
                catch
                {
                    ReleaseCapture();
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopCaptureAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        // 先让迟到的回调失效；释放连接前等记录器刷新缓冲，保持记录帧顺序。
        Interlocked.Increment(ref generation);
        var cleanup = new ResourceCleanup();
        await cleanup.RunAsync(() => new ValueTask(StopCaptureCoreAsync())).ConfigureAwait(false);
        if (consumersCancellation is not null)
        {
            await cleanup.RunAsync(() => new ValueTask(consumersCancellation.CancelAsync())).ConfigureAwait(false);
        }
        if (session is not null)
        {
            await cleanup.RunAsync(session.DisposeAsync).ConfigureAwait(false);
        }
        await cleanup.RunAsync(() => new ValueTask(Task.WhenAll(consumers))).ConfigureAwait(false);
        cleanup.Run(() => logSubscription?.Dispose());
        cleanup.Run(() => plotSubscription?.Dispose());
        cleanup.Run(() => consumersCancellation?.Dispose());
        session = null;
        logSubscription = null;
        plotSubscription = null;
        consumersCancellation = null;
        consumers = [];
        cleanup.ThrowIfFailed("模拟会话清理失败。");
    }

    private async Task StopCaptureCoreAsync()
    {
        if (captureTask is null)
        {
            return;
        }
        var cleanup = new ResourceCleanup();
        try
        {
            await captureCancellation!.CancelAsync().ConfigureAwait(false);
            await captureTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (captureCancellation!.IsCancellationRequested)
        {
            // 取消是记录结束；CaptureFile 的 finally 已经刷新写入缓冲。
        }
        catch (Exception ex)
        {
            cleanup.Record(ex);
            cleanup.Run(() => Diagnostic?.Invoke("记录失败：" + ex));
        }
        finally
        {
            var dropped = captureSubscription?.DroppedFrames ?? 0;
            cleanup.Run(ReleaseCapture);
            cleanup.Run(() => Diagnostic?.Invoke($"记录停止；丢帧 {dropped}。"));
        }
        cleanup.ThrowIfFailed("模拟记录器清理失败。");
    }

    private void ReleaseCapture()
    {
        var subscription = captureSubscription;
        var cancellation = captureCancellation;
        captureTask = null;
        captureSubscription = null;
        captureCancellation = null;
        var cleanup = new ResourceCleanup();
        cleanup.Run(() => subscription?.Dispose());
        cleanup.Run(() => cancellation?.Dispose());
        cleanup.ThrowIfFailed("模拟记录器资源释放失败。");
    }

    private async Task ConsumeLogAsync(FrameSubscription subscription, long connectionGeneration, CancellationToken token)
    {
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref logFrames);
                LogReceived?.Invoke(new(connectionGeneration, frame.Sequence, Encoding.UTF8.GetString(frame.Payload.Span)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 停止连接只取消本服务自己的观察者。
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("设备日志：" + ex);
        }
    }

    private async Task ConsumePlotAsync(FrameSubscription subscription, long connectionGeneration, CancellationToken token)
    {
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref plotFrames);
                var parts = Encoding.UTF8.GetString(frame.Payload.Span).Trim().Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var value) &&
                    int.TryParse(parts[1], CultureInfo.InvariantCulture, out var state))
                {
                    var dropped = subscription.DroppedFrames + (logSubscription?.DroppedFrames ?? 0);
                    PlotReceived?.Invoke(new(connectionGeneration, frame.Sequence, value, state, dropped));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 停止连接只取消本服务自己的观察者。
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("设备绘图：" + ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}

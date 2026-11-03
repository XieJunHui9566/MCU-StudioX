using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using StudioX.Engine.Debugging;
using StudioX.Engine.Pic;
using StudioX.Foundation;

internal static class DebuggerPipeChecks
{
    public static Task<CheckResult[]> RunAsync(string output) => Task.WhenAll(
        Run("mdb-blocked-write-timeout", () => MdbDeadlineAsync(output, "write", false)),
        Run("mdb-blocked-queue-timeout", () => MdbDeadlineAsync(output, "queue", false)),
        Run("mdb-response-timeout", () => MdbDeadlineAsync(output, "response", false)),
        Run("mdb-blocked-write-cancel", () => MdbDeadlineAsync(output, "cancel", true)),
        Run("mdb-response-and-shutdown", () => MdbSuccessAsync(output)),
        Run("mdb-full-pipe-shutdown", () => MdbShutdownAsync(output, false)),
        Run("mdb-concurrent-active-shutdown", () => MdbShutdownAsync(output, true)),
        Run("gdb-blocked-write-timeout", () => GdbDeadlineAsync(output, "write", false)),
        Run("gdb-blocked-queue-timeout", () => GdbDeadlineAsync(output, "queue", false)),
        Run("gdb-blocked-write-cancel", () => GdbDeadlineAsync(output, "cancel", true)),
        Run("gdb-normal-response", () => GdbSuccessAsync(output)),
        Run("process-blocked-stdin-timeout", ProcessDeadlineAsync),
        Run("serial-burst-disconnect-and-send-cancel", () => LifecycleChecks.SerialAsync(output)),
        Run("terminal-large-paste-shutdown", LifecycleChecks.TerminalAsync));

    private static async Task<CheckResult> Run(string name, Func<Task<string>> check)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var detail = await check();
            return new(name, true, watch.ElapsedMilliseconds, detail);
        }
        catch (Exception error) { return new(name, false, watch.ElapsedMilliseconds, error.ToString()); }
    }

    private static async Task<string> MdbDeadlineAsync(string output, string mode, bool cancel)
    {
        await using var fixture = await PipeFixture.CreateAsync(output, "mdb-" + mode, false);
        if (mode == "queue")
        {
            await fixture.Gate.WaitAsync();
        }
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.CancelAfter(200);
        }
        var command = mode == "response" || mode == "queue" ? "version" : new string('x', 8192);
        var run = fixture.Mdb!.ExecuteAsync(command, cancellation.Token, cancel ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(200));
        try
        {
            await ExpectDeadlineAsync(run, cancel, TimeSpan.FromSeconds(2));
            if (mode == "queue" && fixture.Observer.HasExited)
            {
                throw new InvalidOperationException("排队超时不应终止其他命令所属进程。");
            }
        }
        finally
        {
            if (mode == "queue")
            {
                fixture.Gate.Release();
            }
            await fixture.StopAsync(run);
        }
        return cancel ? "user cancellation preserved" : "queue/write/response bounded by 200 ms deadline";
    }

    private static async Task<string> MdbSuccessAsync(string output)
    {
        await using var fixture = await PipeFixture.CreateAsync(output, "mdb-success", false, "mdb");
        await fixture.Mdb!.ExecuteAsync("version", timeout: TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Mdb.ExecuteAsync("version", timeout: TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Mdb.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7));
        fixture.ProductDisposed = true;
        if (!fixture.Observer.HasExited)
        {
            throw new InvalidOperationException("退出后子进程仍在运行。");
        }
        return "echo barriers and graceful exit passed";
    }

    private static async Task<string> MdbShutdownAsync(string output, bool active)
    {
        await using var fixture = await PipeFixture.CreateAsync(output, active ? "mdb-active-close" : "mdb-full-close", false);
        Task<string>? command = null;
        if (active)
        {
            command = fixture.Mdb!.ExecuteAsync(new string('x', 8192), timeout: TimeSpan.FromMinutes(1));
            await Task.Delay(50);
        }
        else
        {
            // Windows 匿名管道容量为 4096 字节；填满后 quit 的刷新会等待读取者。
            await fixture.Child.StandardInput.BaseStream.WriteAsync(new byte[4096]).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        var close = fixture.Mdb!.DisposeAsync().AsTask();
        try
        {
            if (active)
            {
                await Task.WhenAll(close, fixture.Mdb.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(7));
            }
            else
            {
                await close.WaitAsync(TimeSpan.FromSeconds(7));
            }
            fixture.ProductDisposed = true;
            if (!fixture.Observer.HasExited)
            {
                throw new InvalidOperationException("退出没有回收本会话子进程。");
            }
            if (command is not null)
            {
                var failure = await ObserveAsync(command);
                if (failure is null || failure.ToString().Contains("SemaphoreSlim", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("关闭与活动命令的资源释放发生竞态。", failure);
                }
            }
        }
        finally { await fixture.StopAsync(close, command); }
        return "shutdown completed, own process exited, concurrent commands released";
    }

    private static async Task<string> GdbDeadlineAsync(string output, string mode, bool cancel)
    {
        await using var fixture = await PipeFixture.CreateAsync(output, "gdb-" + mode, true);
        if (mode == "queue")
        {
            await fixture.Gate.WaitAsync();
        }
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.CancelAfter(200);
        }
        var command = "1-interpreter-exec console " + (mode == "queue" ? "version" : new string('x', 8192));
        var run = fixture.Gdb!.ExecuteAsync(command, cancellation.Token);
        try
        {
            await ExpectDeadlineAsync(run, cancel, cancel ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(23));
        }
        finally
        {
            if (mode == "queue")
            {
                fixture.Gate.Release();
            }
            await fixture.StopAsync(run);
        }
        return cancel ? "user cancellation preserved" : "queue/write bounded by production 20 s deadline";
    }

    private static async Task<string> GdbSuccessAsync(string output)
    {
        await using var fixture = await PipeFixture.CreateAsync(output, "gdb-success", true, "gdb");
        var first = await fixture.Gdb!.ExecuteAsync("1-gdb-version").WaitAsync(TimeSpan.FromSeconds(2));
        var second = await fixture.Gdb.ExecuteAsync("2-gdb-version").WaitAsync(TimeSpan.FromSeconds(2));
        if (first != "1^done" || second != "2^done")
        {
            throw new InvalidOperationException("MI token 应答配对错误。");
        }
        return "MI tokens matched independent responses";
    }

    private static async Task<string> ProcessDeadlineAsync()
    {
        var start = Program.ChildStart("idle");
        var arguments = start.ArgumentList.ToArray();
        var result = await new ProcessRunner().RunAsync(new(start.FileName, arguments, Environment.CurrentDirectory,
            TimeSpan.FromMilliseconds(800), StandardInput: new string('x', 2 * 1024 * 1024), ContainProcessTree: true)).WaitAsync(TimeSpan.FromSeconds(4));
        if (!result.TimedOut)
        {
            throw new InvalidOperationException("阻塞输入未报告超时。");
        }
        return "2 MiB blocked stdin timed out and owned process tree reclaimed";
    }

    private static async Task ExpectDeadlineAsync(Task operation, bool cancel, TimeSpan watchdog)
    {
        var failure = await ObserveAsync(operation).WaitAsync(watchdog);
        if (cancel ? failure is not OperationCanceledException : failure is not TimeoutException)
        {
            throw new InvalidOperationException("截止时间或用户取消的异常类型错误。", failure);
        }
    }

    private static async Task<Exception?> ObserveAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception error) { return error; }
    }

    /// <summary>直接注入本工具创建的管道，绕过生产构造函数中的 SDK 和探针获取，执行生产通信与关闭方法。</summary>
    private sealed class PipeFixture : IAsyncDisposable
    {
        public required Process Child
        {
            get; init;
        }
        public required Process Observer
        {
            get; init;
        }
        public required SemaphoreSlim Gate
        {
            get; init;
        }
        public required StreamWriter Trace
        {
            get; init;
        }
        public required IDisposable Job
        {
            get; init;
        }
        public required CancellationTokenSource Lifetime
        {
            get; init;
        }
        public MdbProcess? Mdb
        {
            get; init;
        }
        public GdbProcessTransport? Gdb
        {
            get; init;
        }
        public List<Task> Readers { get; } = [];
        public bool ProductDisposed
        {
            get; set;
        }

        public static async Task<PipeFixture> CreateAsync(string output, string name, bool gdb, string mode = "idle")
        {
            var child = Process.Start(Program.ChildStart(mode))!;
            try
            {
                if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) != "ready")
                {
                    throw new IOException("假调试器未就绪。");
                }
                var gate = new SemaphoreSlim(1, 1);
                var trace = new StreamWriter(Path.Combine(output, name + ".log")) { AutoFlush = true };
                var lifetime = new CancellationTokenSource();
                var jobType = typeof(MdbProcess).Assembly.GetType("StudioX.Engine.Debugging.DebugProcessJob")!;
                var job = (IDisposable)Activator.CreateInstance(jobType)!;
                jobType.GetMethod("Add")!.Invoke(job, [child]);
                var product = RuntimeHelpers.GetUninitializedObject(gdb ? typeof(GdbProcessTransport) : typeof(MdbProcess));
                Set(product, gdb ? "gdb" : "process", child);
                Set(product, gdb ? "writes" : "commands", gate);
                Set(product, "log", trace);
                Set(product, "job", job);
                var observer = Process.GetProcessById(child.Id);
                observer.EnableRaisingEvents = true;
                var fixture = new PipeFixture
                {
                    Child = child,
                    Observer = observer,
                    Gate = gate,
                    Trace = trace,
                    Job = job,
                    Lifetime = lifetime,
                    Mdb = product as MdbProcess,
                    Gdb = product as GdbProcessTransport
                };
                if (gdb)
                {
                    Set(product, "pending", new ConcurrentDictionary<int, TaskCompletionSource<string>>());
                    Set(product, "logSync", new object());
                    Set(product, "ready", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    fixture.Readers.Add((Task)Invoke(product, "DrainAsync", child.StandardOutput, "GDB", 0));
                }
                else
                {
                    Set(product, "lifetime", lifetime);
                    Set(product, "disposalSync", new object());
                    Set(product, "sync", new object());
                    Set(product, "buffer", new StringBuilder());
                    Set(product, "stopBuffer", new StringBuilder());
                    var stdout = (Task)Invoke(product, "ReadAsync", child.StandardOutput, false);
                    var stderr = (Task)Invoke(product, "ReadAsync", child.StandardError, true);
                    Set(product, "stdout", stdout);
                    Set(product, "stderr", stderr);
                    fixture.Readers.AddRange([stdout, stderr]);
                }
                return fixture;
            }
            catch
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                }
                child.Dispose();
                throw;
            }
        }

        public async Task StopAsync(params Task?[] operations)
        {
            if (Gdb is not null)
            {
                Set(Gdb, "closing", true);
            }
            if (!Observer.HasExited)
            {
                Observer.Kill(true);
            }
            await Observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var operation in operations)
            {
                if (operation is not null)
                {
                    await ObserveAsync(operation).WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Job.Dispose();
            await Task.WhenAll(Readers).WaitAsync(TimeSpan.FromSeconds(3));
            if (!ProductDisposed)
            {
                Gate.Dispose();
                Trace.Dispose();
                Lifetime.Dispose();
            }
            Child.Dispose();
            Observer.Dispose();
        }

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
        private static object Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments)!;
    }
}

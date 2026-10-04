namespace StudioX.SecurityValidation;

using System.Diagnostics;
using StudioX.Foundation;

/// <summary>回调故障、取消和超时都必须结束工具进程树，并保留原始故障。</summary>
internal static class ProcessChecks
{
    internal static async Task RunFixtureAsync(string marker)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--process-child");
        using var child = Process.Start(start)!;
        await JsonStore.WriteAsync(marker, new[] { Environment.ProcessId, child.Id });
        Console.WriteLine("fixture-ready");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
    }

    internal static async Task RunAsync(string root)
    {
        foreach (var mode in new[] { "observer", "observer-cancel", "cancel", "timeout" })
        {
            var marker = Path.Combine(root, mode + "-processes.json");
            Exception expected = mode == "observer-cancel" ? new OperationCanceledException("fixture-original-observer-cancellation") :
                new IOException("fixture-original-observer-failure");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(text =>
            {
                ready.TrySetResult();
                if (mode.StartsWith("observer", StringComparison.Ordinal))
                {
                    throw expected;
                }
            });
            var timer = Stopwatch.StartNew();
            var run = new ProcessRunner().RunAsync(new(Environment.ProcessPath!, ["--process-fixture", marker], root,
                mode == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15), Output: progress), cancellation.Token);
            int[] ids = [];
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ids = await JsonStore.ReadAsync<int[]>(marker);
                if (mode == "cancel")
                {
                    cancellation.Cancel();
                }
                try
                {
                    var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
                    if (mode != "timeout" || !result.TimedOut)
                    {
                        throw new InvalidOperationException("Expected timeout or original observer exception.");
                    }
                }
                catch (Exception error) when (mode.StartsWith("observer", StringComparison.Ordinal) && ReferenceEquals(error, expected)) { }
                catch (OperationCanceledException) when (mode == "cancel" && cancellation.IsCancellationRequested) { }
                if (ids.Any(IsRunning))
                {
                    throw new InvalidOperationException("Tool failure left its owned process tree running: " + mode);
                }
                Console.WriteLine("PASS process " + mode + " cleanup in " + timer.ElapsedMilliseconds + " ms");
            }
            finally
            {
                cancellation.Cancel();
                // 故障夹具也必须自清理，避免改前复现把测试进程留在宿主。
                foreach (var id in ids)
                {
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
                try
                {
                    await run;
                }
                catch (Exception error) when (ReferenceEquals(error, expected)) { }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}

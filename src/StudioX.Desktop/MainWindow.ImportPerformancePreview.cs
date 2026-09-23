namespace StudioX.Desktop;

using System.Diagnostics;
using System.Windows.Threading;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离副本上采样真实 UI 调度间隔；导入配置和索引，但不编译或连接硬件。</summary>
    public async Task MeasureImportPerformanceAsync(string directory, string fixture)
    {
        var measurements = new List<object>();
        async Task Measure(string phase, Func<Task> action)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var clock = Stopwatch.StartNew();
            var previous = TimeSpan.Zero;
            var gaps = new List<double>();
            var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            void Tick(object? sender, EventArgs args)
            {
                var now = clock.Elapsed; gaps.Add((now - previous).TotalMilliseconds); previous = now;
            }
            timer.Tick += Tick; timer.Start();
            try
            {
                await action();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            finally
            {
                timer.Stop(); timer.Tick -= Tick;
                gaps.Add((clock.Elapsed - previous).TotalMilliseconds);
                var sorted = gaps.Order().ToArray();
                measurements.Add(new { phase, elapsedMs = clock.Elapsed.TotalMilliseconds, ticks = gaps.Count - 1,
                    maxUiGapMs = sorted[^1], p95UiGapMs = sorted[(int)((sorted.Length - 1) * .95)], gapsOver100Ms = gaps.Count(gap => gap > 100) });
                await JsonStore.WriteAsync(Path.Combine(directory, "responsiveness.json"), measurements);
            }
        }
        await Measure("inspect-cold", async () => { await services.CubeMx.InspectAsync(fixture); });
        await Measure("inspect-warm", async () => { await services.CubeMx.InspectAsync(fixture); });
        await Measure("open-configure-index", () => OpenFromCommandLineAsync(fixture));
        if (activeDocument?.RelativePath != "Core/Src/main.c" || !services.Intelligence.IsReady)
            throw new InvalidOperationException("Imported editor/index unavailable: " + Status.Text);
        var offset = SourceEditor.Text.IndexOf("HAL_Init();", StringComparison.Ordinal) + 3;
        var locations = await services.Intelligence.NavigateAsync("Core/Src/main.c", SourceEditor.Text, offset, true);
        if (locations.Count == 0) throw new InvalidOperationException("HAL declaration lookup failed.");
        await CloseProjectAsync(CancellationToken.None);
        await Measure("cancel-from-ui", async () =>
        {
            var stopped = false;
            var progress = new Progress<string>(message =>
            {
                if (!message.StartsWith("完整校验", StringComparison.Ordinal)) return;
                stopped = CancelButton.IsEnabled; Cancel_Click(CancelButton, new System.Windows.RoutedEventArgs());
            });
            await RunAsync(token => services.Toolsets.ResolveAsync(CubeMxImportService.ToolsetId, CubeMxImportService.ToolsetVersion,
                CubeMxImportService.CompilerId, token, forceVerification: true, progress: progress));
            if (!stopped || Status.Text != "操作已取消" || CancelButton.IsEnabled)
                throw new InvalidOperationException("UI cancellation did not stop verification and restore controls.");
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: measured cold/warm inspection and project open/configure/index; editor, HAL navigation and close work; UI Stop cancels verification and restores controls; no firmware build or hardware access.\n");
    }
}

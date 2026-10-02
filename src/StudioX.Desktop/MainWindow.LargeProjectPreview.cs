namespace StudioX.Desktop;

using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>只在指定隔离目录枚举文件，测量实际 WPF 调度延迟，不配置工具或连接设备。</summary>
    public async Task MeasureLargeProjectAsync(string directory, string fixture)
    {
        projectDirectory = fixture;
        WindowProjectTitle.Text = "大工程性能验证 · 30,000 文件";
        var measurements = new List<object>();
        async Task Measure(string phase, Func<Task> action)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var watch = Stopwatch.StartNew();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var previous = TimeSpan.Zero;
            var gaps = new List<double>();
            var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += Tick;
            void Tick(object? sender, EventArgs args)
            {
                var now = watch.Elapsed;
                gaps.Add((now - previous).TotalMilliseconds);
                previous = now;
            }
            timer.Start();
            try { await action(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
            finally
            {
                timer.Stop();
                timer.Tick -= Tick;
                gaps.Add((watch.Elapsed - previous).TotalMilliseconds);
                measurements.Add(new { phase, elapsedMs = watch.Elapsed.TotalMilliseconds, maxUiGapMs = gaps.Max(), ticks = gaps.Count - 1,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, workingSetBytes = Process.GetCurrentProcess().WorkingSet64 });
                await JsonStore.WriteAsync(Path.Combine(directory, "responsiveness.json"), measurements);
            }
        }
        await Measure("tree-initial-wide-directory", () => PopulateProjectTreeAsync("大工程性能验证"));
        var source = await FindProjectNodeAsync("src") ?? throw new InvalidOperationException("Missing source directory");
        await Measure("tree-collapse-reexpand", async () => { source.IsExpanded = false; source.IsExpanded = true; await LoadChildrenAsync(source); });
        if (source.Items.Count != 6000) { throw new InvalidOperationException("Source entries missing"); }
        var first = (TreeViewItem)source.Items[0];
        first.IsSelected = true;
        await Measure("tree-repeat-expansion-selection", async () => { source.IsExpanded = false; source.IsExpanded = true; await LoadChildrenAsync(source); });
        if (!ReferenceEquals(source.Items[0], first)) { throw new InvalidOperationException("Re-expansion recreated nodes"); }
        first.IsSelected = true;
        StudioX.Application.Editing.WorkspaceFileIndex? index = null;
        await Measure("quick-open-cold", async () => { index = await services.WorkspaceDiscovery.CreateIndexAsync(fixture); await index.SearchAsync("source5999"); });
        await Measure("quick-open-second-query", async () => { await index!.SearchAsync("header079"); });
        await Measure("tree-refresh-preserves-selection", () => RefreshProjectTreeAsync());
        source = await FindProjectNodeAsync("src") ?? throw new InvalidOperationException("Missing refreshed directory");
        if (source.Items.Count != 6000 || SelectedProjectEntry?.RelativePath != "src/source0000.c") { throw new InvalidOperationException("Refresh lost source nodes or selection"); }
        // 从调度线程取消正在挂入的大目录，随后重试必须重新取得完整列表。
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = false;
            var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); cancellation.Cancel(); };
            timer.Start();
            try { await PopulateProjectTreeAsync("取消验证", token: cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            finally { timer.Stop(); }
            if (!cancelled) { throw new InvalidOperationException("UI cancellation did not stop wide directory loading"); }
        }
        await PopulateProjectTreeAsync("大工程性能验证");
        source = await FindProjectNodeAsync("src") ?? throw new InvalidOperationException("Retry source directory missing");
        if (source.Items.Count != 6000) { throw new InvalidOperationException("Cancelled load cached a partial directory"); }
        UpdateLayout();
        Render(this, Path.Combine(directory, "large-project.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: 30,000-file discovery, 6,000 source nodes, cached expansion preserving node identity, refresh preserving selection, UI cancellation/retry of wide tree and window-scoped quick-open index; no build or hardware access.\n");
        CancelProjectTreeLoading();
        projectDirectory = null;
    }

    public async Task MeasureProjectOpenPerformanceAsync(string directory, string fixture)
    {
        var watch = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var gaps = new List<double>();
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += Tick;
        void Tick(object? sender, EventArgs args) { var now = watch.Elapsed; gaps.Add((now - previous).TotalMilliseconds); previous = now; }
        timer.Start();
        try
        {
            await OpenFromCommandLineAsync(fixture);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            gaps.Add((watch.Elapsed - previous).TotalMilliseconds);
            if (activeDocument is null || !services.Intelligence.IsReady) { throw new InvalidOperationException("Editor/index unavailable: " + Status.Text); }
            await JsonStore.WriteAsync(Path.Combine(directory, "project-open.json"), new { elapsedMs = watch.Elapsed.TotalMilliseconds, maxUiGapMs = gaps.Max(), ticks = gaps.Count - 1,
                workingSetBytes = Process.GetCurrentProcess().WorkingSet64, document = activeDocument.RelativePath, memoryAnalysisInBackground = buildMemoryCancellation is not null,
                buildEnabled = BuildButton.IsEnabled, cancelEnabled = CancelButton.IsEnabled });
            if (buildMemoryCancellation is not null && (!BuildButton.IsEnabled || !CancelButton.IsEnabled)) { throw new InvalidOperationException("Background self-check blocks actions or cannot cancel"); }
            // 自检尚在进行时编辑、撤销和取消，确认打开流程已经交还工作台。
            var original = SourceEditor.Text;
            SourceEditor.Document.Insert(0, "// responsiveness test\n");
            SourceEditor.Undo();
            if (SourceEditor.Text != original) { throw new InvalidOperationException("Editor unavailable during background self-check"); }
            if (buildMemoryCancellation is not null)
            {
                Cancel_Click(CancelButton, new System.Windows.RoutedEventArgs());
                await buildMemoryRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (buildMemoryCancellation is not null || !BuildButton.IsEnabled || CancelButton.IsEnabled || !BuildMemory.AnalysisStatus.Text.StartsWith("已取消", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Self-check cancel did not restore actions/status");
                }
            }
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "project-open.png"));
            QueueBuildMemoryRefresh(fixture);
            var obsolete = buildMemoryRefreshTask;
            if (!await CloseProjectAsync(CancellationToken.None)) { throw new InvalidOperationException("Cannot close fixture"); }
            await obsolete.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (projectDirectory is not null || BuildMemory.AnalysisStatus.Text != "打开工程并编译后显示占用。") { throw new InvalidOperationException("Old self-check overwrote closed project state"); }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: isolated ESP-IDF editor and real clangd ready before background memory self-check; editing/undo works, Stop cancels self-check without blocking Build, close cancels analysis and rejects late callbacks; no firmware build or hardware access.\n");
        }
        finally { timer.Stop(); timer.Tick -= Tick; }
    }
}

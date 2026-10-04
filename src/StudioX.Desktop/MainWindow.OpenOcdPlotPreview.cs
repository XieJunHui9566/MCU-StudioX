namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.OpenOcdPlot;

public partial class MainWindow
{
    public async Task RenderOpenOcdPlotPreviewAsync(string directory)
    {
        Width = 1450;
        Height = 960;
        BottomRow.Height = new GridLength(100);
        var source = new PreviewPlotSource();
        await using var capture = new OpenOcdPlotService(source);
        OpenOcdPlotPanel.Attach(capture);
        ShowDocument(OpenOcdPlotTab);
        var view = OpenOcdPlotPanel;
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Click(Button button)
        {
            Check(button.IsEnabled, "enabled button: " + button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.PendingOperation;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        foreach (var name in new[] { "sensor.voltage", "control.output", "app_counter" })
        {
            view.ExpressionInput.Text = name;
            await Click(view.AddButton);
        }
        Check(view.ChannelsGrid.Items.Count == 3, "three named channels and addresses rendered");
        view.IntervalInput.Text = "20";
        await Click(view.StartButton);
        await Task.Delay(650);
        view.RefreshView();
        Check(!view.AddButton.IsEnabled && !view.IntervalInput.IsEnabled && view.StopButton.IsEnabled, "capturing freezes configuration and permits stop");
        Check(capture.Snapshot().Records.Length >= 10, "UI start action produces a live trace");
        view.Chart.Zoom(false, .8);
        Check(view.FollowCheck.IsChecked == false, "zoom disables follow checkbox consistently");
        view.Chart.ResetView();
        Check(view.FollowCheck.IsChecked == true, "reset restores follow");
        await Click(view.StopButton);
        view.Chart.Zoom(false, .1, 0);
        Check(!capture.Snapshot().Capturing && view.ExportButton.IsEnabled, "UI stop retains exportable samples");
        await File.WriteAllTextAsync(Path.Combine(directory, "preview.csv"), capture.ExportCsv());
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            view.RefreshTheme();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(view.Chart.ActualHeight >= 230 && view.StartButton.ActualWidth > 0, theme.Id + " chart and controls laid out");
            Check(view.Chart.VisibleSampleCount >= 10, theme.Id + " waveform samples are inside the visible time window");
            Render(view, Path.Combine(directory, "openocd-plot-" + theme.Id + ".png"));
        }
        Width = 1100;
        Height = 760;
        ApplyTheme(ThemeService.Dark);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(view.PageScroll.ScrollableHeight > 0, "compact window scrolls to every control");
        Render(this, Path.Combine(directory, "openocd-plot-compact.png"));
        source.Failure = true;
        await Click(view.StartButton);
        await Task.Delay(80);
        view.RefreshView();
        Check(capture.Snapshot().Error is not null && view.StatusLabel.Foreground.ToString() == FindResource("DiagnosticError").ToString(), "raw read failure is visible in severity color");
        source.Failure = false;
        await Click(view.StartButton);
        await view.CloseSessionAsync();
        Check(!capture.Snapshot().Capturing, "closing tab stops capture");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
        OpenOcdPlotPanel.Attach(services.OpenOcdPlot);
    }

    private sealed class PreviewPlotSource : IOpenOcdPlotSource
    {
        private int sample;
        public bool Failure
        {
            get; set;
        }
        public Guid PlotSessionId { get; } = Guid.NewGuid();
        public bool CanPlot => true;
        public string PlotTarget => "离线界面验证 · 模拟数据 · 未连接芯片";
        public Task<OpenOcdPlotChannel> ResolvePlotChannelAsync(string expression, PlotScalar type, CancellationToken token = default) =>
            Task.FromResult(new OpenOcdPlotChannel(PlotSessionId, expression, expression switch
            {
                "sensor.voltage" => 0x20000010,
                "control.output" => 0x20000014,
                _ => 0x20000018
            }, PlotScalar.Float32, true));
        public Task<OpenOcdPlotReading> ReadPlotAsync(IReadOnlyList<OpenOcdPlotChannel> channels, CancellationToken token = default)
        {
            if (Failure)
            {
                throw new IOException("模拟读取失败 / target not halted (offline test)");
            }
            var x = Interlocked.Increment(ref sample) * .25;
            return Task.FromResult(new OpenOcdPlotReading(DateTimeOffset.Now, true, [2 + Math.Sin(x), 2 + Math.Cos(x) * .6, x % 4]));
        }
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>通过真实按钮运行内置仿真器，验证波形及失效提示；此入口不访问硬件。</summary>
    public async Task RenderHdlWorkflowPreviewAsync(string output, string fixture)
    {
        var checks = new List<string>();
        void Check(bool condition, string text)
        {
            if (!condition)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
        }
        async Task LayoutAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Render);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        await ShowAg32PinMappingAsync(CancellationToken.None);
        Check(Ag32PinMapping.WorkflowButton.Visibility == Visibility.Visible, "自定义工程显示构建仿真入口");
        Ag32PinMapping.WorkflowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(HdlWorkflowTab.IsSelected && HdlWorkflow.BuildSources.Text.Contains("user_logic.v"), "打开入口加载真实工程配置");
        HdlWorkflow.RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        var result = HdlWorkflow.Result ?? throw new InvalidOperationException(HdlWorkflow.Summary.Text);
        var wave = result.Waveform;
        var page = HdlWaveformPage;
        var surface = page.Surface;
        Check(HdlWaveformTab.IsSelected && wave.EndTick > 0 && !HdlWorkflow.Summary.IsVisible, "真实 Icarus 结果自动打开独立波形文档");
        Check(wave.Signals.Any(signal => signal.Width > 1), "波形包含多位总线");
        if (result.Settings.TestbenchTop == "tb_logic")
        {
            Check(wave.EndTick == 125000 && wave.Signals.Any(signal => signal.Width == 2), "标准夹具的时长和两位寄存器正确");
        }
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await LayoutAsync();
            Render(this, Path.Combine(output, "waveform-" + theme.Id + ".png"));
        }
        Check(surface.SpanTicks == wave.EndTick && page.SignalScroll.ViewportHeight > page.ActualHeight * .65,
            "初始显示全程，图表占据文档大部分高度");
        Check(page.FullRangeHint.IsVisible && !page.TimeScroll.IsEnabled, "初始平移栏可见且明确显示全程");
        var width = surface.ActualWidth;
        page.ZoomInButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.SpanTicks < wave.EndTick && surface.ActualWidth == width, "放大缩短可见时间范围，画布宽度稳定");
        await LayoutAsync();
        var track = (Track)page.TimeScroll.Template.FindName("PART_Track", page.TimeScroll);
        Check(page.TimeScroll.IsEnabled && !page.FullRangeHint.IsVisible && track.ActualWidth > width / 2 &&
            track.Thumb.ActualWidth >= 30 && track.ActualHeight >= 24, "平移轨道占据主要宽度，滑块与点击区域清晰");
        var initial = surface.StartTick;
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(track.ActualWidth / 8, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Check(surface.StartTick > initial, "实际滑块拖动会移动可见时间窗口");
        page.TimeScroll.Value = 0;
        page.LaterButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.StartTick == surface.SpanTicks / 10, "下一格按当前时基微调");
        page.EarlierButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.StartTick == 0 && !page.EarlierButton.IsEnabled, "上一格返回起点并禁用越界操作");
        page.ZoomOutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.SpanTicks == wave.EndTick, "缩小恢复全程");
        Check(HandleTextMouseWheel(surface, 120, true) && surface.SpanTicks < wave.EndTick, "工作台将 Ctrl 滚轮路由到波形时基");
        Check(!HandleTextMouseWheel(surface, -120, false), "普通滚轮仍可上下浏览信号");
        page.SignalScroll.ScrollToBottom();
        await LayoutAsync();
        Check(page.SignalScroll.VerticalOffset == page.SignalScroll.ScrollableHeight, "可浏览末尾的匹配信号");
        page.SignalScroll.ScrollToTop();
        page.FitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var signal = wave.Signals.First(s => s.Width == 1 && s.Changes.Length > 2);
        page.SignalFilter.Text = signal.Name;
        await LayoutAsync();
        Check(surface.MinHeight < 150, "按完整层级名称筛选信号");
        page.SignalFilter.Clear();
        page.Timebase.SelectedIndex = 0;
        Check(surface.SpanTicks == 10, "最小时基为一个原始 VCD tick 每格");
        page.TimeScroll.Value = page.TimeScroll.Maximum;
        Check(surface.StartTick + surface.SpanTicks == wave.EndTick, "水平滚动可查看末尾十个 tick");
        surface.MoveCursor(new Point(width, 0), 0);
        Check(page.CursorText.Text.Contains(HdlWaveformSurface.FormatTime(wave.EndTick * wave.NanosecondsPerTick)), "光标读数使用当前窗口的末尾时间");
        page.ZoomOutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.StartTick + surface.SpanTicks == wave.EndTick, "以末尾光标为锚点缩放");
        page.FitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(surface.StartTick == 0 && surface.SpanTicks == wave.EndTick, "显示全程同时复位时基与位置");
        var middle = signal.Changes.Length / 2;
        var tick = signal.Changes[middle].Tick;
        var targetSpan = (decimal)(tick - signal.Changes[middle - 1].Tick) * 20;
        while (surface.SpanTicks > targetSpan && page.ZoomInButton.IsEnabled)
        {
            page.ZoomInButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        page.TimeScroll.Value = Math.Max(0, tick - surface.SpanTicks / 2);
        var center = width * .75;
        surface.MoveCursor(new Point(center, 0), Array.IndexOf(wave.Signals, signal));
        ApplyTheme(ThemeService.Dark);
        await LayoutAsync();
        Render(this, Path.Combine(output, "waveform-detail.png"));
        var accent = ThemeService.Dark with
        {
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        ApplyTheme(accent);
        System.Windows.Application.Current.Resources["WorkbenchFontSize"] = 18d;
        await LayoutAsync();
        Render(this, Path.Combine(output, "waveform-detail-large.png"));
        var start = surface.StartTick;
        await CloseWorkspaceTabAsync(HdlWaveformTab);
        ShowDocument(HdlWorkflowTab);
        HdlWorkflow.WaveformButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(HdlWaveformTab.IsSelected && start == surface.StartTick, "关闭波形后从配置页重开并保留时间窗口");
        ShowDocument(HdlWorkflowTab);
        HdlWorkflow.Duration.Text += "0";
        Check(HdlWorkflow.Summary.Text.Contains("上次仿真快照") && page.StaleText.Visibility == Visibility.Visible, "修改参数后两页均标记历史波形");
        HdlWorkflow.Duration.Text = result.Settings.DurationNanoseconds.ToString();
        HdlWorkflow.ConfigurationPanel.IsExpanded = true;
        await LayoutAsync();
        Render(this, Path.Combine(output, "configuration.png"));
        RefreshAg32LogicUi(currentProjectManifest! with
        {
            Logic = null
        });
        Check(HdlWorkflowTab.Visibility == Visibility.Collapsed && HdlWaveformTab.Visibility == Visibility.Collapsed && HdlWorkflow.Result is null,
            "普通工程隐藏仿真入口及波形文档并清空旧结果");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = checks.Count,
            wave.EndTick,
            wave.NanosecondsPerTick,
            signalCount = wave.Signals.Length,
            checks
        });
    }
}

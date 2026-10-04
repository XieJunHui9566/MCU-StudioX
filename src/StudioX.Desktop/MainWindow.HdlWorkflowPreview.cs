namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
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
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        await ShowAg32PinMappingAsync(CancellationToken.None);
        Check(Ag32PinMapping.WorkflowButton.Visibility == Visibility.Visible, "自定义工程显示构建仿真入口");
        Ag32PinMapping.WorkflowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(HdlWorkflowTab.IsSelected && HdlWorkflow.BuildSources.Text.Contains("user_logic.v"), "打开入口加载真实工程配置");
        HdlWorkflow.RunButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(HdlWorkflow.Result?.Waveform.EndTick == 125000, "界面运行真实 Icarus testbench");
        Check(HdlWorkflow.Result!.Waveform.Signals.Any(signal => signal.Width == 2), "波形包含两位寄存器总线");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await LayoutAsync();
            Render(this, Path.Combine(output, "waveform-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        HdlWorkflow.SignalFilter.Text = "dut";
        await LayoutAsync();
        Check(HdlWorkflow.Surface.Height > 200, "层级信号筛选可用");
        var width = HdlWorkflow.Surface.Width;
        HdlWorkflow.Surface.Zoom(1.5);
        Check(HdlWorkflow.Surface.Width > width, "时间轴缩放有效");
        HdlWorkflow.Duration.Text += "0";
        Check(HdlWorkflow.Summary.Text.Contains("上次仿真快照"), "修改参数明确标记历史波形");
        HdlWorkflow.Duration.Text = HdlWorkflow.Result.Settings.DurationNanoseconds.ToString();
        HdlWorkflow.ConfigurationPanel.IsExpanded = true;
        await LayoutAsync();
        Render(this, Path.Combine(output, "configuration.png"));
        RefreshAg32LogicUi(currentProjectManifest! with
        {
            Logic = null
        });
        Check(HdlWorkflowTab.Visibility == Visibility.Collapsed && HdlWorkflow.Result is null, "普通工程隐藏仿真入口并清空旧波形");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = checks.Count,
            checks
        });
    }
}

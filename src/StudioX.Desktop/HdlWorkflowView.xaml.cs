namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Hdl;

/// <summary>编辑构建与仿真设置；实际工具调用由应用服务执行。</summary>
public partial class HdlWorkflowView : UserControl
{
    private bool loading;
    public bool BuildDirty
    {
        get; private set;
    }
    public HdlSimulationResult? Result
    {
        get; private set;
    }
    public event EventHandler? SaveBuildRequested;
    public event EventHandler? RunRequested;
    public event EventHandler? EditBenchRequested;
    public event EventHandler? CreateBenchRequested;
    public event EventHandler? LogRequested;
    public event EventHandler? TimingReportRequested;
    public HdlWorkflowView()
    {
        InitializeComponent();
        Surface.SetWaveform(null);
        Surface.CursorChanged += text => CursorText.Text = text;
    }
    public void Load(Ag32NativeBuildSettings build, HdlSimulationSettings simulation)
    {
        loading = true;
        BuildSources.Text = string.Join('\n', build.Sources);
        BuildIncludes.Text = string.Join('\n', build.IncludeDirectories);
        BuildDefines.Text = string.Join('\n', build.Defines);
        SdcFiles.Text = string.Join('\n', build.SdcFiles);
        SimulationSources.Text = string.Join('\n', simulation.Sources);
        SimulationIncludes.Text = string.Join('\n', simulation.IncludeDirectories);
        SimulationDefines.Text = string.Join('\n', simulation.Defines);
        TestbenchFile.Text = simulation.TestbenchFile;
        TestbenchTop.Text = simulation.TestbenchTop;
        Duration.Text = simulation.DurationNanoseconds.ToString(CultureInfo.InvariantCulture);
        Timeout.Text = simulation.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        BuildDirty = false;
        loading = false;
    }
    public Ag32NativeBuildSettings ReadBuild() => new(1, Lines(BuildSources), Lines(BuildIncludes), Lines(BuildDefines), Lines(SdcFiles));
    public HdlSimulationSettings ReadSimulation() => new(1, Lines(SimulationSources), Lines(SimulationIncludes), Lines(SimulationDefines),
        TestbenchFile.Text.Trim(), TestbenchTop.Text.Trim(), long.Parse(Duration.Text, CultureInfo.InvariantCulture), int.Parse(Timeout.Text, CultureInfo.InvariantCulture));
    public void MarkBuildSaved() => BuildDirty = false;
    public void SetBusy(bool busy)
    {
        BuildSettingsPanel.IsEnabled = SimulationSettingsPanel.IsEnabled = !busy;
    }
    public void ClearResult(string message)
    {
        Result = null;
        Surface.SetWaveform(null);
        Summary.Text = message;
        LogButton.IsEnabled = false;
        CursorText.Text = "点击定位，按住左键拖动时间光标；筛选结果最多显示 128 条。";
    }
    public void ShowResult(HdlSimulationResult result)
    {
        Result = result;
        Surface.SetWaveform(result.Waveform);
        Surface.Filter(SignalFilter.Text);
        Summary.Text = $"RTL 仿真完成 · {result.Waveform.Signals.Length} 条数字信号 · {result.Waveform.EndTick * result.Waveform.NanosecondsPerTick:0.###} ns · {result.Warnings.Length} 条警告\nVCD：{result.VcdPath}" +
            (result.Warnings.Length == 0 ? "" : "\n" + string.Join("\n", result.Warnings.Take(3)));
        LogButton.IsEnabled = true;
        ConfigurationPanel.IsExpanded = false;
    }
    private static string[] Lines(TextBox box) => box.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private void BuildChanged(object sender, TextChangedEventArgs e)
    {
        if (!loading)
        {
            BuildDirty = true;
        }
    }
    public void MarkStale()
    {
        if (Result is not null)
        {
            Summary.Text = "上次仿真快照：源码或配置已变化，请重新运行。\nVCD：" + Result.VcdPath;
        }
    }
    private void SimulationChanged(object sender, TextChangedEventArgs e)
    {
        if (!loading)
        {
            MarkStale();
        }
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        Surface?.Filter(SignalFilter.Text);
    }
    private void SaveBuild_Click(object sender, RoutedEventArgs e) => SaveBuildRequested?.Invoke(this, EventArgs.Empty);
    private void Run_Click(object sender, RoutedEventArgs e) => RunRequested?.Invoke(this, EventArgs.Empty);
    private void EditBench_Click(object sender, RoutedEventArgs e) => EditBenchRequested?.Invoke(this, EventArgs.Empty);
    private void CreateBench_Click(object sender, RoutedEventArgs e) => CreateBenchRequested?.Invoke(this, EventArgs.Empty);
    private void Log_Click(object sender, RoutedEventArgs e) => LogRequested?.Invoke(this, EventArgs.Empty);
    private void TimingReport_Click(object sender, RoutedEventArgs e) => TimingReportRequested?.Invoke(this, EventArgs.Empty);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.Zoom(1.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.Zoom(1 / 1.5);
}

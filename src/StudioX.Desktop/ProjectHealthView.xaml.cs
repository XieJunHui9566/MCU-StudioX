namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Health;

public partial class ProjectHealthView : UserControl
{
    public Func<bool, Task>? InspectRequested
    {
        get; set;
    }
    public Func<Task>? ChooseRequested
    {
        get; set;
    }
    public Func<Task>? ExportRequested
    {
        get; set;
    }
    public Func<Task>? ToolsRequested
    {
        get; set;
    }
    public Func<HealthCheck, Task>? ActionRequested
    {
        get; set;
    }
    public Func<string, Task>? HelpRequested
    {
        get; set;
    }
    public Func<HealthCheck, bool>? CanAct
    {
        get; set;
    }
    public ProjectHealthReport? Report
    {
        get; private set;
    }
    public HealthCheck? SelectedCheck => ChecksGrid.SelectedItem as HealthCheck;

    public ProjectHealthView()
    {
        InitializeComponent();
    }
    public void SetReport(ProjectHealthReport report)
    {
        Report = report;
        ProjectText.Text = report.ProjectName + " · " + report.Target + "\n" + (report.ProjectDirectory ?? "未选择工程");
        StatusText.Text = report.Summary;
        ChecksGrid.ItemsSource = report.Checks.OrderBy(check => check.State == HealthState.Error ? 0 : check.State == HealthState.Warning ? 1 : 2).ToArray();
        ChecksGrid.SelectedIndex = 0;
        ExportButton.IsEnabled = true;
        UpdateSelection();
    }
    public void SetBusy(bool value, string? progress = null)
    {
        Toolbar.IsEnabled = !value;
        if (progress is not null)
        {
            StatusText.Text = progress;
        }
        UpdateSelection();
        ActionButton.IsEnabled &= !value;
        HelpButton.IsEnabled &= !value;
    }
    public void Invalidate()
    {
        Report = null;
        ChecksGrid.ItemsSource = null;
        ProjectText.Text = "工程已切换，请重新检查。";
        StatusText.Text = "旧工程的检查结果已清除。";
        ExportButton.IsEnabled = false;
        UpdateSelection();
    }
    public void BeginInspection()
    {
        Report = null;
        ChecksGrid.ItemsSource = null;
        ExportButton.IsEnabled = false;
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        var check = SelectedCheck;
        DetailText.Text = check is null ? "选择检查项查看详细说明。" : $"{check.Title} · {check.StateText}"
            + (check.State is HealthState.Error or HealthState.Warning ? "\n诊断编号：" + check.Code : "") + "\n\n" + check.Detail
            + (check.RawDiagnostic.Length == 0 ? "" : "\n\n原始诊断：\n" + check.RawDiagnostic);
        ActionButton.Content = check?.ActionText ?? "处理所选问题";
        ActionButton.IsEnabled = check is not null && Toolbar.IsEnabled && (CanAct?.Invoke(check) ?? true);
        HelpButton.IsEnabled = check is not null && Toolbar.IsEnabled;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DetailText is not null)
        {
            UpdateSelection();
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (InspectRequested is not null)
        {
            await InspectRequested(false);
        }
    }
    private async void Deep_Click(object sender, RoutedEventArgs e)
    {
        if (InspectRequested is not null)
        {
            await InspectRequested(true);
        }
    }
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseRequested is not null)
        {
            await ChooseRequested();
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ExportRequested is not null)
        {
            await ExportRequested();
        }
    }
    private async void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsRequested is not null)
        {
            await ToolsRequested();
        }
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCheck is { } check && ActionRequested is not null)
        {
            await ActionRequested(check);
        }
    }
    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCheck is { } check && HelpRequested is not null)
        {
            await HelpRequested(check.HelpTopic);
        }
    }
}

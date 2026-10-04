namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Tools;

public partial class ToolManagementView : UserControl
{
    public ToolManagementView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => DescriptionText.Visibility = ActualHeight < 500 ? Visibility.Collapsed : Visibility.Visible;
    }
    public ToolManagementReport? Report
    {
        get; private set;
    }
    public ManagedToolVersion? SelectedVersion => VersionsGrid.SelectedItem as ManagedToolVersion;
    public Func<string, Task>? Requested
    {
        get; init;
    }
    private bool busy;
    public void SetReport(ToolManagementReport report)
    {
        Report = report;
        var selection = SelectedVersion;
        VersionsGrid.ItemsSource = report.Versions;
        StatusText.Text = report.Summary.Split('。')[0] + "。";
        StatusText.ToolTip = report.Summary;
        VersionsGrid.SelectedItem = report.Versions.FirstOrDefault(version => selection is not null && version.Id == selection.Id && version.Version == selection.Version && version.RetirementId == selection.RetirementId)
            ?? report.Versions.FirstOrDefault();
        UpdateButtons();
        if (report.Diagnostics.Count > 0)
        {
            DetailText.Text = "依赖检查未完成，清理已禁用。\n\n" + string.Join("\n\n", report.Diagnostics);
        }
    }
    public void SetBusy(bool value, string? text = null)
    {
        busy = value;
        Toolbar.IsEnabled = SelectionActions.IsEnabled = !busy;
        if (text is not null)
        {
            StatusText.Text = text;
        }
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var selected = SelectedVersion;
        ExportButton.IsEnabled = !busy && Report is not null;
        RecoveryButton.Visibility = Report?.Recoveries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryButton.IsEnabled = !busy && Report?.Recoveries.Count > 0;
        VerifyButton.IsEnabled = !busy && selected is { Installed: true } && selected.CompilerId.Length > 0;
        ToggleButton.IsEnabled = !busy && selected?.CanToggle == true;
        ToggleButton.Content = selected?.Enabled == false ? "启用" : "禁用";
        DebugCheckButton.IsEnabled = !busy && selected is { Installed: true, Enabled: true, Busy: false } && selected.CompilerId.Length > 0;
        RetireButton.IsEnabled = !busy && selected?.CanRemove == true;
        RestoreButton.IsEnabled = !busy && selected?.CanRestore == true;
        RestoreButton.Visibility = selected is { Installed: false } ? Visibility.Visible : Visibility.Collapsed;
        PurgeButton.IsEnabled = !busy && selected?.CanDelete == true;
        ComponentExportButton.IsEnabled = RepairButton.IsEnabled = !busy && selected is { Installed: true, Busy: false, SafeToManage: true };
        SelectionText.Text = selected is null ? "选择一个开发环境组件版本" : $"所选组件：{selected.Name} · {selected.Id} / {selected.Version}";
        ManagementHint.Text = selected?.ManagementHint ?? "禁用、删除和校验只作用于所选版本。";
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DetailText is null)
        {
            return;
        }
        if (SelectedVersion is { } selected)
        {
            DetailText.Text = $"{selected.Id} / {selected.Version} · {selected.StateText}\n编译器：{selected.CompilerId}\n{selected.SizeText} · {selected.Files:N0} 个文件\n组件：{selected.Components}\n\n"
                + string.Join('\n', selected.References) + "\n\n" + selected.Diagnostic
                + "\n\n只检查当前、最近和登记工程，以及已安装器件包与内置功能。其他工程请先登记。"
                + "\n" + selected.ManagementHint
                + (!selected.Installed ? "\n可恢复区仍占磁盘空间；永久删除后不可恢复。" : "");
            if (!selected.Enabled)
            {
                DetailText.Text += "\n该版本已禁用：文件与工程锁保留，需要它的工程无法启动工具。可在此重新启用。";
            }
        }
        else
        {
            DetailText.Text = "尚未选择开发环境组件。导入 .mcutoolchain 或从 GitHub 获取工程需要的精确版本。";
        }
        UpdateButtons();
    }
    private async Task RequestAsync(string action)
    {
        if (!busy && Requested is not null)
        {
            await Requested(action);
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RequestAsync("refresh");
    private async void Install_Click(object sender, RoutedEventArgs e) => await RequestAsync("install");
    private async void Register_Click(object sender, RoutedEventArgs e) => await RequestAsync("register");
    private async void Scope_Click(object sender, RoutedEventArgs e) => await RequestAsync("scope");
    private async void Export_Click(object sender, RoutedEventArgs e) => await RequestAsync("export");
    private async void Help_Click(object sender, RoutedEventArgs e) => await RequestAsync("help");
    private async void Verify_Click(object sender, RoutedEventArgs e) => await RequestAsync("verify");
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await RequestAsync("toggle");
    private async void DebugCheck_Click(object sender, RoutedEventArgs e) => await RequestAsync("debug-check");
    private async void Retire_Click(object sender, RoutedEventArgs e) => await RequestAsync("retire");
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RequestAsync("restore");
    private async void Purge_Click(object sender, RoutedEventArgs e) => await RequestAsync("purge");
    private async void Github_Click(object sender, RoutedEventArgs e) => await RequestAsync("github");
    private async void Prepare_Click(object sender, RoutedEventArgs e) => await RequestAsync("prepare");
    private async void ComponentExport_Click(object sender, RoutedEventArgs e) => await RequestAsync("component-export");
    private async void Repair_Click(object sender, RoutedEventArgs e) => await RequestAsync("repair");
    private async void Recovery_Click(object sender, RoutedEventArgs e) => await RequestAsync("recovery");
    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

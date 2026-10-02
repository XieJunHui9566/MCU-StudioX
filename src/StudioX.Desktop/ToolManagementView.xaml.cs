namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Tools;

public partial class ToolManagementView : UserControl
{
    public ToolManagementView() => InitializeComponent();
    public ToolManagementReport? Report { get; private set; }
    public ManagedToolVersion? SelectedVersion => VersionsGrid.SelectedItem as ManagedToolVersion;
    public Func<string, Task>? Requested { get; init; }
    private bool busy;
    public void SetReport(ToolManagementReport report)
    {
        Report = report;
        VersionsGrid.ItemsSource = report.Versions;
        StatusText.Text = report.Summary;
        VersionsGrid.SelectedIndex = report.Versions.Count > 0 ? 0 : -1;
        UpdateButtons();
        if (report.Diagnostics.Count > 0) DetailText.Text = "依赖检查未完成，清理已禁用。\n\n" + string.Join("\n\n", report.Diagnostics);
    }
    public void SetBusy(bool value, string? text = null)
    {
        busy = value; Toolbar.IsEnabled = !busy;
        if (text is not null) StatusText.Text = text;
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var selected = SelectedVersion;
        ExportButton.IsEnabled = !busy && Report is not null;
        VerifyButton.IsEnabled = !busy && selected is { Installed: true } && selected.CompilerId.Length > 0;
        RetireButton.IsEnabled = !busy && selected?.CanRetire == true;
        RestoreButton.IsEnabled = !busy && selected?.CanRestore == true;
        PurgeButton.IsEnabled = !busy && selected?.CanPurge == true;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DetailText is null) return;
        if (SelectedVersion is { } selected)
        {
            DetailText.Text = $"{selected.Id} / {selected.Version} · {selected.StateText}\n编译器：{selected.CompilerId}\n{selected.SizeText} · {selected.Files:N0} 个文件\n组件：{selected.Components}\n\n"
                + string.Join('\n', selected.References) + "\n\n" + selected.Diagnostic
                + "\n\n只检查当前、最近和登记工程，以及已安装器件包与内置功能。其他工程请先登记。"
                + (selected.Latest ? "\n保留每种工具的最新已安装版本。" : "")
                + (!selected.Installed ? "\n可恢复区仍占磁盘空间；永久删除后不可恢复。" : "");
        }
        UpdateButtons();
    }
    private async Task RequestAsync(string action) { if (Requested is not null) await Requested(action); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RequestAsync("refresh");
    private async void Install_Click(object sender, RoutedEventArgs e) => await RequestAsync("install");
    private async void Register_Click(object sender, RoutedEventArgs e) => await RequestAsync("register");
    private async void Scope_Click(object sender, RoutedEventArgs e) => await RequestAsync("scope");
    private async void Export_Click(object sender, RoutedEventArgs e) => await RequestAsync("export");
    private async void Help_Click(object sender, RoutedEventArgs e) => await RequestAsync("help");
    private async void Verify_Click(object sender, RoutedEventArgs e) => await RequestAsync("verify");
    private async void Retire_Click(object sender, RoutedEventArgs e) => await RequestAsync("retire");
    private async void Restore_Click(object sender, RoutedEventArgs e) => await RequestAsync("restore");
    private async void Purge_Click(object sender, RoutedEventArgs e) => await RequestAsync("purge");
}

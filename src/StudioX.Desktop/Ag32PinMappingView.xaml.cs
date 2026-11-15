namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;

/// <summary>展示基础映射状态；不生成工具参数或持有设备。</summary>
public partial class Ag32PinMappingView : UserControl
{
    public Ag32PinMappingView() => InitializeComponent();

    public event EventHandler? EnableRequested;
    public event EventHandler? SchematicRequested;
    public event EventHandler? WorkflowRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? LicenseRequested;
    public event EventHandler? DownloadToolsRequested;
    public event EventHandler? LicenseGuideRequested;

    public void SetState(bool enabled, bool customLogic, bool busy, string details, bool missingSource = false)
    {
        DescriptionText.Text = customLogic
            ? "联合构建 MCU 固件与 FPGA 位流；需配置 Supra 许可。"
            : "编译与下载包含固件和引脚映射；需配置 Supra 许可。";
        StatusText.Text = details;
        EnableButton.Visibility = (!enabled || missingSource) && !customLogic ? Visibility.Visible : Visibility.Collapsed;
        EnableButton.Content = enabled && missingSource ? "恢复 .ve 模板" : "启用基础映射";
        EnableButton.IsEnabled = !busy;
        OpenButton.IsEnabled = (customLogic || enabled && !missingSource) && !busy;
        SchematicButton.Visibility = customLogic ? Visibility.Visible : Visibility.Collapsed;
        SchematicButton.IsEnabled = !busy;
        WorkflowButton.Visibility = customLogic ? Visibility.Visible : Visibility.Collapsed;
        WorkflowButton.IsEnabled = !busy;
        LicenseButton.Visibility = Visibility.Visible;
        LicenseButton.IsEnabled = !busy;
        LicenseHelp.Visibility = DocumentationActions.Visibility = Visibility.Visible;
        Planner.Visibility = enabled && !missingSource && !customLogic ? Visibility.Visible : Visibility.Collapsed;
        Planner.SetBusy(busy);
    }

    private void Enable_Click(object sender, RoutedEventArgs e) => EnableRequested?.Invoke(this, EventArgs.Empty);
    private void Schematic_Click(object sender, RoutedEventArgs e) => SchematicRequested?.Invoke(this, EventArgs.Empty);
    private void Workflow_Click(object sender, RoutedEventArgs e) => WorkflowRequested?.Invoke(this, EventArgs.Empty);
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void License_Click(object sender, RoutedEventArgs e) => LicenseRequested?.Invoke(this, EventArgs.Empty);
    private void DownloadTools_Click(object sender, RoutedEventArgs e) => DownloadToolsRequested?.Invoke(this, EventArgs.Empty);
    private void LicenseGuide_Click(object sender, RoutedEventArgs e) => LicenseGuideRequested?.Invoke(this, EventArgs.Empty);
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;

/// <summary>展示基础映射状态并打开双镜像确认窗口；不生成工具参数或持有设备。</summary>
public partial class Ag32PinMappingView : UserControl
{
    public Ag32PinMappingView() => InitializeComponent();

    internal Ag32DownloadConfirmationWindow? ActiveDownloadConfirmation { get; private set; }

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
            ? "顶部编译会联合构建 MCU 固件和自定义 FPGA 位流；下载时核对两段镜像并分别校验。可配置 Verilog 源文件与 SDC，并运行 RTL testbench 查看波形。Supra 需配置本机有效许可。"
            : "C 代码操作内部 GPIO；编辑 .ve 绑定封装引脚。点击顶部“编译”同时生成固件与映射镜像，点击顶部“下载”核对两份镜像后写入并校验。基础映射无需 Quartus，Supra 需要有效厂商许可；许可只保存到本机用户目录。";
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

    public async Task<bool> ConfirmDownloadAsync(string details, Func<bool> isCurrent, CancellationToken token)
    {
        if (token.IsCancellationRequested || !isCurrent() || ActiveDownloadConfirmation is not null ||
            Window.GetWindow(this) is not { IsVisible: true } owner)
        {
            return false;
        }
        var dialog = new Ag32DownloadConfirmationWindow(details) { Owner = owner };
        ActiveDownloadConfirmation = dialog;
        try
        {
            // 保持异步调用契约；窗口拥有独立的模态消息循环，取消仍可由调度器送达。
            return await Dispatcher.InvokeAsync(() => dialog.Confirm(isCurrent, token),
                System.Windows.Threading.DispatcherPriority.Background) && !token.IsCancellationRequested && isCurrent();
        }
        finally
        {
            ActiveDownloadConfirmation = null;
        }
    }

    private void Enable_Click(object sender, RoutedEventArgs e) => EnableRequested?.Invoke(this, EventArgs.Empty);
    private void Schematic_Click(object sender, RoutedEventArgs e) => SchematicRequested?.Invoke(this, EventArgs.Empty);
    private void Workflow_Click(object sender, RoutedEventArgs e) => WorkflowRequested?.Invoke(this, EventArgs.Empty);
    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void License_Click(object sender, RoutedEventArgs e) => LicenseRequested?.Invoke(this, EventArgs.Empty);
    private void DownloadTools_Click(object sender, RoutedEventArgs e) => DownloadToolsRequested?.Invoke(this, EventArgs.Empty);
    private void LicenseGuide_Click(object sender, RoutedEventArgs e) => LicenseGuideRequested?.Invoke(this, EventArgs.Empty);
}

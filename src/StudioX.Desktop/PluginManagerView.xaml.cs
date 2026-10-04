namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;

/// <summary>展示插件目录、显式启停和一次性审批；插件运行与文件事务交给应用服务。</summary>
public partial class PluginManagerView : UserControl
{
    private PluginManagerService? service;
    private PluginCatalogEntry[] entries = [];
    private CancellationTokenSource? operation;
    private Task pendingOperation = Task.CompletedTask;
    private bool stopped;
    private readonly HashSet<string> settingsPlugins = [], debugPlugins = [];
    public Func<string, Task>? ShowSettingsRequestedAsync
    {
        get; set;
    }
    public Func<string, Task>? ShowDebugRequestedAsync
    {
        get; set;
    }
    public Func<CancellationToken, Task>? WorkspaceChangedAsync
    {
        get; set;
    }
    public Action? CancelCommandRequested
    {
        get; set;
    }
    public bool IsOperating => operation is not null;

    public PluginManagerView()
    {
        InitializeComponent();
    }

    public void Attach(PluginManagerService manager)
    {
        service = manager;
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        if (service is null || stopped)
        {
            return;
        }
        entries = (await service.ListAsync(token)).ToArray();
        ShowCatalog();
    }

    private void ShowCatalog()
    {
        if (CatalogList is null || CatalogFilter is null || CapabilityFilter is null)
        {
            return;
        }
        var query = CatalogFilter.Text.Trim();
        var capability = (CapabilityFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var rows = entries.Select(entry => new PluginCatalogRow(entry, operation is not null || stopped, settingsPlugins.Contains(entry.Id), debugPlugins.Contains(entry.Id)))
            .Where(row => (capability.Length == 0 || row.Entry.Capabilities.Contains(capability)) &&
                (row.DisplayName + " " + row.Entry.Id + " " + row.Description + " " + row.CapabilityText).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        CatalogList.ItemsSource = rows;
        CatalogSummary.Text = $"显示 {rows.Length} / {entries.Length} 个插件";
        EmptyHint.Text = entries.Length == 0 ? "未安装工作台插件。" : "没有匹配的插件，可清空筛选条件。";
        EmptyHint.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = RefreshButton.IsEnabled = operation is null && !stopped;
        CancelButton.IsEnabled = operation is not null;
    }

    public void SetContributions(IReadOnlyList<PluginActiveContribution> contributions, Func<string, bool> running)
    {
        settingsPlugins.Clear();
        debugPlugins.Clear();
        foreach (var plugin in contributions.Where(p => running(p.Id)))
        {
            if (plugin.Contribution.Settings.Length > 0)
            {
                settingsPlugins.Add(plugin.Id);
            }
            if (plugin.Contribution.DebugAdapters.Length > 0)
            {
                debugPlugins.Add(plugin.Id);
            }
        }
        ShowCatalog();
    }

    private void CatalogFilter_TextChanged(object sender, TextChangedEventArgs e) => ShowCatalog();
    private void CapabilityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCatalog();
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowContributionAsync(sender, ShowSettingsRequestedAsync);
    private async void Debug_Click(object sender, RoutedEventArgs e) => await ShowContributionAsync(sender, ShowDebugRequestedAsync);
    private async Task ShowContributionAsync(object sender, Func<string, Task>? action)
    {
        if (operation is not null || stopped || action is null || (sender as Button)?.Tag is not PluginCatalogRow row)
        {
            return;
        }
        try
        {
            await action(row.Entry.Id);
        }
        catch (Exception error) { Log(error.ToString()); OperationStatus.Text = "插件入口打开失败：" + error.Message; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "安装工作台插件", Filter = "StudioX 插件|*.studioxplugin", CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            await RunAsync(async token =>
            {
                await service!.ImportAsync(picker.FileName, token);
                OperationStatus.Text = "插件已安装，点击启用后才会运行。";
            });
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PluginCatalogRow row)
        {
            await RunAsync(async token =>
            {
                await service!.SetEnabledAsync(row.Entry.Id, !row.Entry.Enabled, token);
                if (WorkspaceChangedAsync is not null)
                {
                    await WorkspaceChangedAsync(token);
                }
                OperationStatus.Text = row.Entry.Enabled ? "插件已停用。" : "已允许运行插件；正在当前工程内加载贡献。";
            });
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PluginCatalogRow row)
        {
            await RunAsync(async token =>
            {
                await service!.UninstallAsync(row.Entry.Id, token);
                if (WorkspaceChangedAsync is not null)
                {
                    await WorkspaceChangedAsync(token);
                }
                OperationStatus.Text = "插件已卸载。";
            });
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    private void CancelCommand_Click(object sender, RoutedEventArgs e) => CancelCommandRequested?.Invoke();

    private void CommandFilter_TextChanged(object sender, TextChangedEventArgs e) => FilterCommands();

    public void FilterCommands()
    {
        if (CommandsHost is null || CommandFilter is null)
        {
            return;
        }
        foreach (var button in CommandsHost.Children.OfType<Button>())
        {
            button.Visibility = (Convert.ToString(button.Content) ?? "").Contains(CommandFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void FocusCommandFilter()
    {
        CommandFilter.BringIntoView();
        CommandFilter.Focus();
        CommandFilter.SelectAll();
    }

    public void SetCommandRunning(bool running)
    {
        CommandStatus.Text = running ? "插件命令正在执行…" : "";
        CancelCommandButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || service is null || stopped)
        {
            return;
        }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        ShowCatalog();
        OperationStatus.Text = "正在处理插件…";
        try
        {
            pendingOperation = action(cancellation.Token);
            await pendingOperation;
            await RefreshAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            OperationStatus.Text = "插件操作已取消。";
        }
        catch (Exception error)
        {
            Log(error.ToString());
            OperationStatus.Text = "插件操作失败：" + error.Message;
        }
        finally
        {
            operation = null;
            ShowCatalog();
        }
    }

    public void Log(string message)
    {
        DiagnosticLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        if (DiagnosticLog.Text.Length > 100_000)
        {
            DiagnosticLog.Text = "[较早日志已截断]\n" + DiagnosticLog.Text[^80_000..];
        }
        DiagnosticLog.ScrollToEnd();
    }

    public async Task<bool> RequestApprovalAsync(StudioXMcpApprovalRequest request,
        Func<bool> isCurrent, CancellationToken token)
    {
        if (!isCurrent() || token.IsCancellationRequested)
        {
            return false;
        }
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "插件请求执行操作", FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = $"工程：{Bound(request.Project, 350)}\n工具：{Bound(request.Tool, 100)}\n{Bound(request.Summary, 1600)}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 10)
        });
        var card = new Border { Child = body, BorderThickness = new Thickness(1), Padding = new Thickness(12), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        card.SetResourceReference(Border.BackgroundProperty, "ChromeSurface");
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var reject = new Button { Content = "拒绝本次", Padding = new Thickness(12, 5, 12, 5) };
        var approve = new Button
        {
            Content = "允许本次",
            IsEnabled = request.Summary.Length <= 1600 && request.Project.Length <= 350 && request.Tool.Length <= 100,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0)
        };
        reject.Click += (_, _) => choice.TrySetResult(false);
        approve.Click += (_, _) => choice.TrySetResult(isCurrent() && !token.IsCancellationRequested);
        buttons.Children.Add(reject);
        buttons.Children.Add(approve);
        body.Children.Add(buttons);
        ApprovalHost.Children.Add(card);
        card.BringIntoView();
        reject.Focus();
        using var cancelled = token.Register(() => choice.TrySetResult(false));
        try
        {
            return await choice.Task && isCurrent() && !token.IsCancellationRequested;
        }
        finally
        {
            // 审批只存在于等待选择期间，不把已批准卡片留在界面。
            ApprovalHost.Children.Remove(card);
        }
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "…[信息过长，只能拒绝]";

    public async Task ShutdownAsync()
    {
        stopped = true;
        operation?.Cancel();
        try
        {
            await pendingOperation;
        }
        catch (Exception error)
        {
            // 操作回调已展示诊断，继续释放窗口拥有的取消源和贡献。
            Log(error.ToString());
        }
        CommandsHost.Children.Clear();
        PanelsHost.Children.Clear();
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application;

public partial class MainWindow
{
    private TabItem? environmentTab;
    private TabItem? troubleshootingTab;
    private DataGrid? environmentGrid;
    private TextBlock? environmentStatus;
    private string lastFailure = "";
    private Task ShowToolEnvironmentAsync() => RunAsync(async token =>
    {
        if (environmentTab is null)
        {
            var root = new DockPanel { Margin = new(16) };
            var actions = new WrapPanel();
            DockPanel.SetDock(actions, Dock.Top);
            root.Children.Add(actions);
            environmentStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
            DockPanel.SetDock(environmentStatus, Dock.Top);
            root.Children.Add(environmentStatus);
            void Button(string label, Func<Task> action)
            {
                var button = new Button { Content = label, Margin = new(0, 0, 8, 0) };
                button.Click += async (_, _) => await action();
                actions.Children.Add(button);
            }
            Button("刷新", ShowToolEnvironmentAsync);
            Button("校验所选工具集", () => RunEnvironmentActionAsync("verify"));
            Button("导出离线工具包", () => RunEnvironmentActionAsync("export"));
            Button("从离线包修复", () => RunEnvironmentActionAsync("repair"));
            environmentGrid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
            environmentGrid.SetResourceReference(StyleProperty, "DebugGrid");
            foreach (var (label, binding, width) in new[] { ("工具集", "Name", "*"), ("版本", "Version", "120"), ("工程需要", "RequiredText", "90"), ("占用空间", "SizeText", "110"), ("文件", "Files", "80"), ("状态", "Status", "190"), ("组件版本", "Components", "300") })
            {
                environmentGrid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new System.Windows.Data.Binding(binding), Width = (DataGridLength)new DataGridLengthConverter().ConvertFromInvariantString(width)! });
            }
            root.Children.Add(environmentGrid);
            environmentTab = AddToolTab("工具环境", root);
        }
        ShowDocument(environmentTab);
        environmentStatus!.Text = "正在读取版本、空间及工程主工具集依赖…";
        environmentGrid!.ItemsSource = await services.ToolEnvironment.InspectAsync(projectDirectory, token);
        environmentGrid.SelectedIndex = 0;
        environmentStatus.Text = "工具随 IDE 管理，不修改系统 PATH。工程需要列表示工程锁定的 MCU 与引脚映射工具集；修复严格匹配 ID 和版本，保留原工具备份。导出需要先校验完整性。";
    });
    private Task RunEnvironmentActionAsync(string action) => RunAsync(async token =>
    {
        if (environmentGrid?.SelectedItem is not ToolEnvironmentEntry entry)
        {
            return;
        }
        var progress = new Progress<string>(text => environmentStatus!.Text = text);
        if (action == "verify")
        {
            await services.ToolEnvironment.VerifyAsync(entry, progress, token);
            environmentStatus!.Text = "校验通过：" + entry.Name;
        }
        else if (action == "export")
        {
            var dialog = new SaveFileDialog { Filter = "StudioX 离线工具包|*.studioxtools", FileName = entry.Id + "-" + entry.Version + ".studioxtools" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            await services.ToolEnvironment.ExportAsync(entry, dialog.FileName, progress, token);
            environmentStatus!.Text = "离线工具包已导出：" + dialog.FileName;
        }
        else
        {
            if (services.Debugger.IsActive)
            {
                throw new InvalidOperationException("请先结束调试会话再修复工具。");
            }
            var dialog = new OpenFileDialog { Filter = "StudioX 离线工具包|*.studioxtools" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            var backup = await services.ToolEnvironment.RepairAsync(entry, dialog.FileName, progress, token);
            environmentStatus!.Text = "工具已修复。" + (backup.Length > 0 ? "原文件备份：" + backup : "");
        }
    });
    private TabItem AddToolTab(string title, UIElement content)
    {
        var tab = new TabItem { Content = content, Height = 34 };
        tab.Header = CreateTabHeader(tab, new TextBlock { Text = title });
        WorkspaceTabs.Items.Add(tab);
        AttachTabMouseActions(tab);
        return tab;
    }
    private Task ShowTroubleshootingAsync()
    {
        ShowTroubleshooting(lastFailure);
        return Task.CompletedTask;
    }
    private void ShowTroubleshooting(string diagnostic, string? category = null)
    {
        lastFailure = diagnostic;
        var advice = TroubleshootingService.Explain(category ?? diagnostic);
        var root = new DockPanel { Margin = new(18) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = advice.Title, FontSize = 22, Margin = new(0, 0, 0, 10) });
        header.Children.Add(new TextBlock { Text = advice.Explanation, TextWrapping = TextWrapping.Wrap });
        for (var index = 0; index < advice.Steps.Length; index++)
        {
            header.Children.Add(new TextBlock { Text = $"{index + 1}. {advice.Steps[index]}", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        }
        var categories = new WrapPanel { Margin = new(0, 16, 0, 16) };
        foreach (var (title, sample) in new[] { ("工具缺失", "TOOLSET_MISSING"), ("头文件缺失", "file not found"), ("端口占用", "port is busy"), ("连接失败", "调试连接失败"), ("文件冲突", "EDITOR_FILE_CHANGED") })
        {
            var button = new Button { Content = title, Margin = new(0, 0, 8, 0) };
            button.Click += (_, _) => ShowTroubleshooting(diagnostic, sample);
            categories.Children.Add(button);
        }
        header.Children.Add(categories);
        var next = new Button { Content = advice.Action == "tools" ? "打开工具环境" : advice.Action == "problems" ? "查看问题列表" : advice.Action == "serial" ? "查看串口会话" : advice.Action == "history" ? "查看本地历史" : "查看构建日志", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        next.Click += async (_, _) => { if (advice.Action == "tools") { await ShowToolEnvironmentAsync(); } else if (advice.Action == "serial") { ShowDocument(SerialTab); } else if (advice.Action == "history") { await ShowLocalHistoryAsync(); } else { ShowBottom(advice.Action == "problems" ? 4 : 0); } };
        header.Children.Add(next);
        var raw = new TextBox { Text = diagnostic.Length == 0 ? "选择上方问题类别查看处理步骤。" : diagnostic, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas") };
        System.Windows.Automation.AutomationProperties.SetName(raw, "原始诊断");
        root.Children.Add(raw);
        if (troubleshootingTab is null)
        {
            troubleshootingTab = AddToolTab("故障处理", root);
        }
        else
        {
            troubleshootingTab.Content = root;
        }
        ShowDocument(troubleshootingTab);
    }
}

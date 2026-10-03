namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application;

public partial class MainWindow
{
    private TabItem? troubleshootingTab;
    private string lastFailure = "";
    private static string FailureDiagnostic(Exception error) => error is StudioX.Foundation.StudioXException studio
        ? studio.Code + "\n" + error : error.ToString();
    private Task ShowToolEnvironmentAsync() => ShowToolManagementAsync();
    private Task ShowToolEnvironmentForProjectAsync(string? checkedProject) => ShowToolManagementAsync(checkedProject);
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
        var next = new Button { Content = advice.Action == "tool-management" ? "打开开发环境组件管理" : advice.Action == "health" ? "检查工程配置缓存" : advice.Action == "tools" ? "打开开发环境组件" : advice.Action == "problems" ? "查看问题列表" : advice.Action == "serial" ? "查看串口会话" : advice.Action == "history" ? "查看本地历史" : "查看构建日志", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        next.Click += async (_, _) => { if (advice.Action == "tool-management") { await ShowToolManagementAsync(); } else if (advice.Action == "health") { await ShowProjectHealthAsync(diagnostic: diagnostic); } else if (advice.Action == "tools") { await ShowToolEnvironmentAsync(); } else if (advice.Action == "serial") { ShowDocument(SerialTab); } else if (advice.Action == "history") { await ShowLocalHistoryAsync(); } else { ShowBottom(advice.Action == "problems" ? 4 : 0); } };
        header.Children.Add(next);
        var manual = new Button { Content = "查看完整故障处理手册", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        manual.Click += async (_, _) => await ShowHelpAsync(services.Help.DiagnosticTopic(category ?? diagnostic));
        header.Children.Add(manual);
        var health = new Button { Content = "检查工程并打开修复入口", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 12) };
        health.Click += async (_, _) => await ShowProjectHealthAsync(diagnostic: diagnostic);
        header.Children.Add(health);
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

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;

public partial class MainWindow
{
    private TabItem? troubleshootingTab;
    private string lastFailure = "";
    private string? buildFailureProject;
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
    private void ShowTroubleshooting(string diagnostic, string? category = null, bool buildFailure = false)
    {
        lastFailure = diagnostic;
        buildFailureProject = buildFailure ? projectDirectory : null;
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
            button.Click += (_, _) => ShowTroubleshooting(diagnostic, sample, buildFailure);
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
            troubleshootingTab.Header = CreateTabHeader(troubleshootingTab, new TextBlock { Text = "故障处理" });
        }
        ShowDocument(troubleshootingTab);
    }

    private void ResolveBuildTroubleshooting(string directory)
    {
        // 只解除同一工程的构建故障；编译成功不能替串口、连接或文件冲突作验收。
        if (troubleshootingTab is null || buildFailureProject is null ||
            !string.Equals(buildFailureProject, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var diagnostic = lastFailure;
        lastFailure = "";
        buildFailureProject = null;
        var root = new DockPanel { Margin = new(18) };
        var status = new StackPanel { Margin = new(0, 0, 0, 16) };
        DockPanel.SetDock(status, Dock.Top);
        status.Children.Add(new TextBlock { Text = "本次编译已成功", FontSize = 22, Margin = new(0, 0, 0, 10) });
        status.Children.Add(new TextBlock { Text = "下方保留上一轮构建诊断供查阅。当前构建结果见构建输出与问题列表。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(status);
        var raw = new TextBox
        {
            Text = diagnostic,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new("Consolas")
        };
        System.Windows.Automation.AutomationProperties.SetName(raw, "历史构建诊断");
        root.Children.Add(raw);
        troubleshootingTab.Content = root;
        troubleshootingTab.Header = CreateTabHeader(troubleshootingTab, new TextBlock { Text = "历史构建诊断" });
    }
}

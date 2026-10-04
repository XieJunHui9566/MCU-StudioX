namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Debugging;

public sealed class FaultAnalysisView : UserControl
{
    private readonly TextBox input = new() { AcceptsReturn = true, MinHeight = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox output = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public Func<string, Task>? Requested
    {
        get; set;
    }
    public string Input => input.Text;
    internal string ResultText => output.Text;
    public FaultAnalysisReport? Report
    {
        get; private set;
    }
    public FaultAnalysisView()
    {
        var body = new DockPanel { Margin = new(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "固件故障分析", FontSize = 22 });
        top.Children.Add(new TextBlock { Text = "粘贴 ESP Panic/回溯日志，或读取已暂停 Cortex-M3/M4/M7 的故障现场。地址定位使用当前工程 ELF，导入日志需自行确认固件对应。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        var buttons = new WrapPanel();
        foreach (var (title, action) in new[] { ("分析日志", "analyze"), ("读取暂停现场", "read"), ("使用工程 ELF 定位", "locate"), ("转储 + 工程 ELF", "dump"), ("转储 + 归档 ELF", "archive-dump"), ("导入报告", "import"), ("导出报告", "export") })
        {
            var button = new Button { Content = title, Margin = new(0, 0, 8, 8), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) { await run(action); } };
            buttons.Children.Add(button);
        }
        top.Children.Add(buttons);
        top.Children.Add(input);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(output);
        Content = body;
    }
    public void SetReport(FaultAnalysisReport report)
    {
        Report = report;
        output.Text = report.ToText();
    }
    public void SetDiagnostic(string text)
    {
        Report = null;
        output.Text = text;
    }
}

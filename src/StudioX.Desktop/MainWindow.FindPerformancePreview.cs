namespace StudioX.Desktop;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    public async Task RenderFindPerformancePreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        const string sample = "// 中文 😀\r\nvalue 12\r\nvalue 34\r\n";
        ShowSource(new SourceDocument("src/find_preview.txt", sample, Encoding.UTF8, "preview", false));
        ShowFindReplace(true);
        FindText.Text = "value";
        Check(FindStatus.Text.Contains("正在查找", StringComparison.Ordinal), "输入事件仅排队，不同步扫描文本");
        await UpdateFindStatusAsync();
        Check(FindStatus.Text.Contains("2 处", StringComparison.Ordinal), "后台统计包含两处匹配");
        SourceEditor.Select(0, 0);
        await FindMatchAsync(false);
        Check(SourceEditor.SelectionStart == sample.IndexOf("value", StringComparison.Ordinal), "下一处保留 UTF-16 位置");
        await FindMatchAsync(true);
        Check(SourceEditor.SelectionStart == sample.LastIndexOf("value", StringComparison.Ordinal), "上一处从首项回绕至末项");
        FindRegex.IsChecked = true;
        FindText.Text = "value (\\d+)";
        ReplaceText.Text = "counter=$1";
        await ReplaceMatchesAsync(true);
        Check(SourceEditor.Text == "// 中文 😀\r\ncounter=12\r\ncounter=34\r\n", "后台替换计划保留捕获组和 CRLF");
        SourceEditor.Undo();
        Check(SourceEditor.Text == sample, "全部替换仍可一次撤销");
        FindText.Text = "[";
        await ReplaceMatchesAsync(true);
        Check(SourceEditor.Text == sample && FindStatus.Text.Contains("失败", StringComparison.Ordinal), "非法正则不产生部分写入");
        FindRegex.IsChecked = false;
        FindText.Text = "value";
        ReplaceText.Text = "counter";
        await findGate.WaitAsync();
        Task replacement;
        try
        {
            replacement = ReplaceMatchesAsync(true);
            SourceEditor.AppendText("new draft\r\n");
        }
        finally
        {
            findGate.Release();
        }
        await replacement;
        Check(SourceEditor.Text == sample + "new draft\r\n", "搜索期间继续编辑会取消旧替换计划");
        await findGate.WaitAsync();
        Task stale;
        try
        {
            stale = FindMatchAsync(false);
            ShowSource(new SourceDocument("src/other_find.txt", "other document", Encoding.UTF8, "preview", false));
        }
        finally
        {
            findGate.Release();
        }
        await stale;
        Check(SourceEditor.Text == "other document" && SourceEditor.SelectionLength == 0, "切换文件后迟到结果不能修改选择");
        SourceEditor.Text = new string('a', 4000) + "!";
        FindRegex.IsChecked = true;
        FindText.Text = "(a+)+$";
        var clock = Stopwatch.StartNew();
        var pathological = UpdateFindStatusAsync();
        var heartbeat = await Dispatcher.InvokeAsync(() => (Elapsed: clock.Elapsed.TotalMilliseconds, Pending: !pathological.IsCompleted), DispatcherPriority.Normal);
        await pathological;
        Check(heartbeat.Pending, "高回溯正则执行时 UI 调度仍可运行");
        Check(FindStatus.Text.Contains("查找无效", StringComparison.Ordinal), "高回溯正则仍受到原有超时保护");
        SourceEditor.Text = sample;
        FindRegex.IsChecked = false;
        FindText.Text = "value";
        await UpdateFindStatusAsync();
        var custom = ThemeService.Dark with
        {
            Id = "custom-find",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B04FE0",
                ["OnAccent"] = "#FFFFFF"
            }
        };
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
        {
            ApplyTheme(theme);
            if (theme == custom)
            {
                Resources["WorkbenchFontSize"] = 18.0;
            }
            UpdateLayout();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Render);
            Check(FindStatus.IsVisible && ReplaceAllButton.IsVisible && ReplaceAllButton.IsEnabled, theme.Id + " 查找状态与替换按钮可用");
            Render(this, Path.Combine(directory, theme.Id + ".png"));
        }
        CloseFind_Click(this, new RoutedEventArgs());
        Check(!findTimer.IsEnabled && FindPanel.Visibility == Visibility.Collapsed, "关闭查找面板停止待执行扫描");
        // 夹具没有磁盘工程；恢复测试缓冲区后关闭，避免草稿确认窗口阻塞自动验收退出。
        foreach (var document in editorDocuments)
        {
            document.Buffer.Text = document.Source.Text;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true,
            checks,
            heartbeatMilliseconds = heartbeat.Elapsed,
            hardware = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

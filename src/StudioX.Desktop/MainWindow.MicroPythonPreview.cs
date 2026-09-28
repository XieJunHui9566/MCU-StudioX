namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>真实桌面控件与工程切换验证；不枚举或连接硬件。</summary>
    public async Task RenderMicroPythonPreviewAsync(string directory, params string[] archives)
    {
        var results = new List<string>();
        foreach (var archive in archives)
        {
            var pack = await ImportPackForSelectionAsync(archive, CancellationToken.None);
            await BeginNewProjectAsync(CancellationToken.None);
            SelectPack(pack);
            DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
            TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(item => item.Id == "micropython-minimal");
            if (TemplatePicker.Items.Count != 5 || !CreateProjectButton.IsEnabled)
            {
                throw new InvalidOperationException("缺少 C / MicroPython 模板或创建入口。");
            }
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, pack.Manifest.Id + "-templates.png"));
            var project = Path.Combine(directory, pack.Manifest.Id + "-python");
            await services.Projects.CreateAsync(pack, pack.Manifest.Devices.Single().Id, "micropython-minimal", "python_demo", project);
            await OpenProjectAsync(project, CancellationToken.None);
            if (!IsMicroPythonProject || activeDocument?.RelativePath != "main.py" || !IsPythonDocument || services.Intelligence.IsReady ||
                BuildButton.IsEnabled || !DownloadButton.IsEnabled || !DownloadMenu.IsEnabled || !DownloadSettingsButton.IsEnabled || DebugStartButton.IsEnabled ||
                !MicroPythonRunButton.IsEnabled || MicroPythonRunButton.Visibility != Visibility.Visible ||
                StatusLanguage.Text != "MicroPython" || StatusEncoding.Text != "UTF-8")
            {
                throw new InvalidOperationException("MicroPython 入口或操作路由错误。");
            }
            // 从实际顶部按钮进入；没有指定端口时只能提示选择，不能尝试连接设备。
            DownloadButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await pendingOperation;
            if (WorkspaceTabs.SelectedItem != MicroPythonTab || services.MicroPython.IsConnected ||
                !MicroPythonPanel.OperationStatus.Contains("选择或输入", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("顶部下载没有进入串口准备页面。");
            }
            MicroPythonRunButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await microPythonRunTask;
            var start = (Button)MicroPythonPanel.FindName("StartButton");
            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (!start.IsEnabled || services.MicroPython.IsConnected || MicroPythonPanel.IsBusy ||
                !MicroPythonPanel.OperationStatus.Contains("选择或输入", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("运行入口必须先要求明确选择端口。");
            }
            var buffer = new MicroPythonOutputBuffer();
            buffer.Append(new string('a', 128 * 1024));
            buffer.Append("中文尾部");
            var drained = buffer.Drain();
            if (drained.Text.Length != 128 * 1024 || !drained.Text.EndsWith("中文尾部", StringComparison.Ordinal) || drained.Discarded != 4 || buffer.Drain() != ("", 0L))
            {
                throw new InvalidOperationException("持续运行输出必须有界且可观察显示截断。");
            }
            var original = activeDocument!;
            renderingAssistancePreview = true;
            try
            {
                ShowSource(original with
                {
                    Text = "from machine import Pin\nled = Pin('LED', Pin.OUT)\nled.t"
                });
                SourceEditor.CaretOffset = SourceEditor.Text.Length;
                QueueAssistance(signature: false, manual: true);
                await assistTask;
                if (completionWindow?.CompletionList.CompletionData.Any(item => item.Text == "toggle") != true)
                {
                    throw new InvalidOperationException("实际编辑器未提供 MicroPython 对象提示。");
                }
                completionWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(completionWindow, Path.Combine(directory, pack.Manifest.Id + "-completion.png"));
            }
            finally
            {
                CloseCodeAssistance();
                await assistTask;
                renderingAssistancePreview = false;
                ShowSource(original);
            }
            await CheckPythonEditingAsync(directory, pack.Manifest.Id);
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                ShowDocument(MicroPythonTab);
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var ports = (ComboBox)MicroPythonPanel.FindName("PortPicker");
                ports.ItemsSource = new[] { "COM77", "COM78" };
                ports.SelectedItem = "COM77";
                UpdateLayout();
                var editable = ports.Template.FindName("PART_EditableTextBox", ports) as TextBox;
                if (editable is null || editable.Text != "COM77" || !editable.IsVisible || editable.ActualWidth < 30)
                {
                    throw new InvalidOperationException("选中端口未呈现在可见输入框。");
                }
                editable.Text = "COM79";
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                if (ports.Text != "COM79")
                {
                    throw new InvalidOperationException("手动输入端口未同步到下载配置。");
                }
                ports.SelectedItem = "COM78";
                UpdateLayout();
                if (editable.Text != "COM78")
                {
                    throw new InvalidOperationException("切换选中端口后输入框未更新。");
                }
                var execute = (Button)MicroPythonPanel.FindName("ExecuteButton");
                if (!start.IsVisible || start.ActualWidth < 40 || !MicroPythonRunButton.IsVisible)
                {
                    throw new InvalidOperationException("开始运行按钮不可见。");
                }
                var normalScroll = (ScrollViewer)MicroPythonPanel.FindName("PageScroll");
                var executeBottom = execute.TransformToAncestor(normalScroll).Transform(new Point(0, execute.ActualHeight));
                if (executeBottom.Y > normalScroll.ActualHeight + 1)
                {
                    throw new InvalidOperationException("普通窗口应完整呈现 REPL 执行按钮。");
                }
                Render(this, Path.Combine(directory, pack.Manifest.Id + "-repl-" + theme.Id + ".png"));
                var originalWidth = Width;
                var originalHeight = Height;
                Width = 1100;
                Height = 720;
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var scroll = (ScrollViewer)MicroPythonPanel.FindName("PageScroll");
                var output = (TextBox)MicroPythonPanel.FindName("OutputText");
                output.Text = "布局检查：" + string.Concat(Enumerable.Repeat("很长的工程备份路径/", 40)) + "\nEND — 最后一行";
                scroll.ScrollToEnd();
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var bottom = output.TransformToAncestor(scroll).Transform(new Point(0, output.ActualHeight));
                if (scroll.ScrollableHeight <= 0 || bottom.Y > scroll.ActualHeight + 1 || output.ActualHeight < 100 ||
                    output.TextWrapping != TextWrapping.Wrap || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                {
                    throw new InvalidOperationException("小窗口内容被裁切或长日志撑宽页面。");
                }
                Render(this, Path.Combine(directory, pack.Manifest.Id + "-compact-" + theme.Id + ".png"));
                Width = originalWidth;
                Height = originalHeight;
                output.Clear();
                ports.Text = "";
                scroll.ScrollToTop();
            }
            await ShowProjectDetailsAsync();
            if (BuildSettingsCard.Visibility != Visibility.Collapsed || !CurrentProjectToolset.Text.Contains("MicroPython", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("脚本工程仍展示 C 构建参数。");
            }
            await CloseProjectAsync(CancellationToken.None);
            if (DownloadButton.IsEnabled || MicroPythonRunButton.IsEnabled || MicroPythonRunButton.Visibility != Visibility.Collapsed || services.MicroPython.IsConnected)
            {
                throw new InvalidOperationException("关闭工程没有清理 MicroPython 会话状态。");
            }
            results.Add("PASS: " + pack.Manifest.Id + " 5 templates, Python entry, board API completion, toolbar download/start and page start routing without a port, bounded live output display, selected/typed COM text visible in both themes, compact scrollable layout, wrapped long logs, C build/debug disabled, close cleanup. No hardware accessed.");
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "result.txt"), results);
    }

    private async Task CheckPythonEditingAsync(string directory, string packId)
    {
        var original = SourceEditor.Text;
        const string sample = "from machine import Pin\nled = Pin('LED', Pin.OUT)\nled.toggle()\nvalue = 1\nprint(value)\nprint(value)\n";
        try
        {
            SourceEditor.Text = sample;
            SourceEditor.CaretOffset = sample.IndexOf("toggle", StringComparison.Ordinal);
            QueueCodeNavigation(false);
            await navigationTask;
            if (activeDocument?.RelativePath != "@micropython/machine.pyi" || !SourceEditor.IsReadOnly || SourceEditor.SelectedText != "toggle")
            {
                throw new InvalidOperationException("MicroPython 实际编辑器无法跳到只读 API 方法声明。");
            }
            await TravelNavigationAsync(true);
            if (activeDocument?.RelativePath != "main.py" || SourceEditor.Text != sample)
            {
                throw new InvalidOperationException("Python 跳转返回丢失未保存内容。");
            }
            SourceEditor.CaretOffset = sample.IndexOf("value)", StringComparison.Ordinal);
            QueuePythonReferences();
            await navigationTask;
            if (WorkspaceTabs.SelectedItem != PythonReferencesTab || PythonReferences.Items.Count != 3)
            {
                throw new InvalidOperationException("Python 引用列表未显示定义与两处使用。");
            }
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, packId + "-references.png"));
            ShowDocument(FindEditor("main.py")!.Tab);
            ShowFindReplace(true);
            FindText.Text = "value";
            ReplaceText.Text = "counter";
            FindWord.IsChecked = true;
            ReplaceAll_Click(this, new RoutedEventArgs());
            if (SourceEditor.Text != sample.Replace("value", "counter", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("编辑器批量替换未处理所有匹配。");
            }
            SourceEditor.Undo();
            if (SourceEditor.Text != sample)
            {
                throw new InvalidOperationException("批量替换不能一次撤销。");
            }
            FindRegex.IsChecked = true;
            FindText.Text = "[";
            ReplaceAll_Click(this, new RoutedEventArgs());
            if (SourceEditor.Text != sample || !FindStatus.Text.Contains("失败", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"非法正则检查失败：sameText={SourceEditor.Text == sample}, canEdit={CanEditSource}, status={FindStatus.Text}");
            }
            FindRegex.IsChecked = false;
            FindText.Text = "value";
            SourceEditor.Select(0, 0);
            FindMatch(false);
            if (SourceEditor.SelectedText != "value")
            {
                throw new InvalidOperationException("查找没有选中匹配文本。");
            }
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(this, Path.Combine(directory, packId + "-find-replace-" + theme.Id + ".png"));
            }
        }
        finally
        {
            ShowDocument(FindEditor("main.py")!.Tab);
            CloseFind_Click(this, new RoutedEventArgs());
            SourceEditor.Text = original;
            await SaveAllSourcesAsync(RequireProject(), CancellationToken.None);
        }
    }
}

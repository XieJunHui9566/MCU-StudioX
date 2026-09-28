namespace StudioX.Desktop;

using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.MicroPython;
using StudioX.Application.Serial;
using StudioX.Packages;

/// <summary>只呈现会话操作；串口、协议与备份均由应用服务持有。</summary>
public partial class MicroPythonView : UserControl
{
    private MicroPythonSessionService? service;
    private ProjectFileService? files;
    private string? project;
    private MicroPythonProfile? profile;
    private CancellationTokenSource? operation;
    private Task pending = Task.CompletedTask;
    private int projectRevision;
    private string? connectedPort;
    public bool IsBusy => operation is not null;
    public bool IsScriptRunning { get; private set; }
    public string OperationStatus => OperationText.Text;
    public Func<Task>? DownloadRequested
    {
        get; set;
    }
    public event Action? StateChanged;
    public MicroPythonView()
    {
        InitializeComponent();
        UpdateControls();
    }

    public void Attach(MicroPythonSessionService session, ProjectFileService fileService)
    {
        service = session;
        files = fileService;
        session.Diagnostic += text =>
        {
            var revision = projectRevision;
            Dispatcher.BeginInvoke(() => { if (revision == projectRevision) { Append(text); } });
        };
        UpdateControls();
    }

    public void SetProject(string? directory, MicroPythonProfile? settings)
    {
        projectRevision++;
        project = directory;
        profile = settings;
        ProjectText.Text = settings is null ? "请先打开 MicroPython 工程。" :
            $"{settings.Board} · MicroPython {settings.Version} · USB 串口下载与 REPL";
        ScriptPath.Text = "main.py";
        connectedPort = null;
        PortPicker.Text = "";
        PortPicker.ItemsSource = null;
        OperationText.Text = "选择串口后，点击顶部下载或下方下载脚本。";
        OutputText.Clear();
        UpdateControls();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        var selected = PortPicker.Text;
        var ports = await SerialTerminalService.ListPortsAsync();
        PortPicker.ItemsSource = ports;
        // 刷新不能把用户明确选定或手动输入的端口清空，也不能猜测连接对象。
        PortPicker.Text = selected;
        OperationText.Text = ports.Length == 0 ? "未发现串口，请连接开发板后刷新，或手动输入 COM 口。" : "端口列表已刷新，请选择开发板串口。";
    });

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (project is not { } directory || service is null)
        {
            return;
        }
        var port = PortPicker.Text.Trim();
        if (!RequirePort(port))
        {
            return;
        }
        await RunAsync(async token =>
        {
            OperationText.Text = "正在连接 " + port + "…";
            await service.ConnectAsync(directory, port, token);
            connectedPort = port;
            OperationText.Text = "REPL 已连接，可以下载脚本、开始运行或执行片段。";
            Append("连接成功：" + service.Identity);
        });
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        var code = CommandText.Text;
        await RunAsync(async token =>
        {
            OperationText.Text = "正在执行 REPL 片段…";
            Append("> " + code);
            var result = await service!.ExecuteAsync(code, token);
            Append(result.Output + result.Error);
            OperationText.Text = result.Error.Length == 0 ? "REPL 执行完成。" : "REPL 返回异常，详见右侧输出。";
        });
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadRequested is { } download)
        {
            await download();
        }
    }

    public async Task DownloadAsync(CancellationToken token)
    {
        if (project is not { } directory || service is null || files is null)
        {
            return;
        }
        var relative = ScriptPath.Text.Trim();
        var port = PortPicker.Text.Trim();
        if (!service.IsConnected && !RequirePort(port))
        {
            return;
        }
        await RunAsync(async cancellation =>
        {
            MicroPythonSessionService.ValidateScriptPath(relative);
            var source = await files.ReadAsync(directory, relative, cancellation);
            if (!service.IsConnected)
            {
                OperationText.Text = "正在连接 " + port + "…";
                await service.ConnectAsync(directory, port, cancellation);
                connectedPort = port;
                Append("连接成功：" + service.Identity);
            }
            OperationText.Text = "正在备份、下载并校验 " + relative + "…";
            var result = await service.UploadAsync(relative, Encoding.UTF8.GetBytes(source.Text), cancellation);
            OperationText.Text = $"下载完成：{result.Path} · {result.Bytes} 字节 · 校验通过。点击「开始运行」启动。";
            Append($"已下载 {result.Path} · {result.Bytes} 字节\nSHA-256 {result.Sha256}\n备份：{result.BackupPath ?? "板上原先没有此文件"}\n未自动运行脚本。");
        }, token);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartScriptAsync();

    public async Task StartScriptAsync()
    {
        if (project is not { } directory || service is null || IsBusy) { return; }
        var relative = ScriptPath.Text.Trim();
        var port = PortPicker.Text.Trim();
        if (!service.IsConnected && !RequirePort(port)) { return; }
        await RunAsync(async token =>
        {
            MicroPythonSessionService.ValidateScriptPath(relative);
            if (!service.IsConnected)
            {
                OperationText.Text = "正在连接 " + port + "…";
                await service.ConnectAsync(directory, port, token);
                connectedPort = port;
                Append("连接成功：" + service.Identity);
            }
            IsScriptRunning = true;
            UpdateControls();
            OperationText.Text = "正在运行板上 " + relative + "；可点击顶部停止。";
            Append("开始运行板上 " + relative + "（已下载版本）；以下为程序输出：");
            var buffer = new MicroPythonOutputBuffer();
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            void Flush()
            {
                var (text, discarded) = buffer.Drain();
                if (discarded != 0) { Append($"显示缓存已截断 {discarded} 个字符。"); }
                if (text.Length != 0) { AppendOutput(text); }
            }
            timer.Tick += (_, _) => Flush();
            timer.Start();
            try
            {
                var success = await service.RunScriptAsync(relative, buffer.Append, token);
                Flush();
                OperationText.Text = success ? "程序运行结束。" : "程序返回异常，详见输出。";
                Append("\n" + OperationText.Text);
            }
            finally
            {
                timer.Stop();
                Flush();
                IsScriptRunning = false;
            }
        });
    }

    private bool RequirePort(string port)
    {
        if (port.Length != 0)
        {
            return true;
        }
        OperationText.Text = "请先选择或输入开发板 USB 串口，再点击下载或开始运行。";
        PortPicker.BringIntoView();
        PortPicker.Focus();
        return false;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => OutputText.Clear();

    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReplArea is null)
        {
            return;
        }
        var narrow = e.NewSize.Width < 820;
        CommandColumn.Width = new GridLength(narrow ? 1 : 2, GridUnitType.Star);
        OutputColumn.Width = narrow ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        OutputRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(OutputPane, narrow ? 0 : 1);
        Grid.SetRow(OutputPane, narrow ? 1 : 0);
        CommandPane.Margin = narrow ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 12, 0);
        ReplArea.MinHeight = narrow ? 460 : 210;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();
    private void Firmware_Click(object sender, RoutedEventArgs e)
    {
        if (profile is { } settings)
        {
            try
            {
                Process.Start(new ProcessStartInfo(settings.FirmwarePage) { UseShellExecute = true });
            }
            catch (Exception ex) { Append(ex.ToString()); }
        }
    }

    private Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        if (operation is not null)
        {
            return pending;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation = cancellation;
        UpdateControls();
        pending = ExecuteOperationAsync();
        return pending;
        async Task ExecuteOperationAsync()
        {
            try
            {
                await action(cancellation.Token);
            }
            catch (OperationCanceledException ex)
            {
                OperationText.Text = "操作取消或超时；连接已结束。";
                Append(OperationText.Text + "\n" + ex);
            }
            catch (Exception ex)
            {
                OperationText.Text = "操作失败：" + ex.Message;
                Append(ex.ToString());
            }
            finally
            {
                operation = null;
                cancellation.Dispose();
                UpdateControls();
            }
        }
    }

    public async Task StopAsync()
    {
        operation?.Cancel();
        if (service is not null)
        {
            await service.DisconnectAsync();
        }
        await pending;
        connectedPort = null;
        OperationText.Text = "已停止并断开连接。";
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (ConnectButton is null)
        {
            return;
        }
        var ready = project is not null && profile is not null && service is not null;
        var idle = operation is null;
        ConnectButton.IsEnabled = ready && idle && !service!.IsConnected;
        ExecuteButton.IsEnabled = ready && idle && service!.IsConnected;
        UploadButton.IsEnabled = ready && idle;
        StartButton.IsEnabled = ready && idle;
        StartButton.Content = IsScriptRunning ? "运行中…" : "开始运行";
        ScriptPath.IsEnabled = idle;
        RefreshButton.IsEnabled = ready && idle && service?.IsConnected != true;
        FirmwareButton.IsEnabled = ready;
        PortPicker.IsEnabled = idle && service?.IsConnected != true;
        StopButton.IsEnabled = operation is not null || service?.IsConnected == true;
        ConnectionText.Text = service?.IsConnected == true ? connectedPort + " · " + service.Identity : "未连接 · 连接会中断板上程序";
        OperationProgress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        StateChanged?.Invoke();
    }

    private void Append(string text)
        => AppendOutput($"[{DateTimeOffset.Now:HH:mm:ss} 宿主] {text}\n");

    private void AppendOutput(string text)
    {
        if (OutputText.Text.Length + text.Length > 128 * 1024)
        {
            OutputText.Text = "[较早的显示内容已截断]\n";
        }
        OutputText.AppendText(text);
        OutputText.ScrollToEnd();
    }
}

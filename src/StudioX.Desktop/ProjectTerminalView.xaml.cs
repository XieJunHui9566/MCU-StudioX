namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application.Terminal;

public partial class ProjectTerminalView : UserControl
{
    private ProjectTerminalService? service;
    private string? project;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SerialTerminalView.TerminalColorizer colors = new();
    private readonly List<string> history = [];
    private int historyIndex;
    private long version = -1;
    private bool busy;
    private bool hasSession;
    private string? error;
    public ProjectTerminalView()
    {
        InitializeComponent(); OutputEditor.TextArea.TextView.LineTransformers.Add(colors);
        timer.Tick += (_, _) => Refresh(); Loaded += (_, _) => timer.Start(); Unloaded += (_, _) => timer.Stop();
        OutputEditor.PreviewTextInput += async (_, e) => { e.Handled = true; await SendRawAsync(e.Text); };
        OutputEditor.PreviewKeyDown += Output_KeyDown;
        DataObject.AddPastingHandler(CommandInput, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string text && (text.Contains('\n') || text.Contains('\r')))
            { e.CancelCommand(); CommandInput.SelectedText = SingleLine(text); }
        });
    }
    public void Attach(ProjectTerminalService value) => service = value;
    public async Task SetProjectAsync(string? directory)
    {
        if (project == directory) return;
        if (service is not null) await service.StopAsync();
        project = directory; hasSession = false; error = null; history.Clear(); historyIndex = 0; CommandInput.Clear(); SecretInput.Clear();
        ProjectPath.Text = directory ?? "尚未打开工程"; ProjectPath.ToolTip = directory;
        OutputEditor.Clear(); version = -1; StartButton.IsEnabled = directory is not null;
        InterruptButton.IsEnabled = false; CommandInput.IsEnabled = SecretInput.IsEnabled = directory is not null;
        TerminalStatus.Text = directory is null ? "请先创建或打开工程。" : "点击启动，或通过“工具 → 工程终端”进入。";
    }
    public async Task EnsureStartedAsync()
    {
        if (busy || service is null) return;
        if (project is null) { TerminalStatus.Text = "请先创建或打开工程。"; return; }
        if (service.Snapshot().Running) { CommandInput.Focus(); return; }
        busy = true;
        try { error = null; await service.StartAsync(project); hasSession = true; version = -1; Refresh(); CommandInput.Focus(); }
        catch (Exception ex) { Error(ex); }
        finally { busy = false; }
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await EnsureStartedAsync();
    private async void Stop_Click(object sender, RoutedEventArgs e) { if (service is null || busy) return; busy = true; try { await service.StopAsync(); Refresh(); } catch (Exception ex) { Error(ex); } finally { busy = false; } }
    private async void Interrupt_Click(object sender, RoutedEventArgs e) => await SendRawAsync("\x03");
    private void Clear_Click(object sender, RoutedEventArgs e) { service?.ClearHistory(); Refresh(); }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    private async Task SubmitAsync()
    {
        if (service?.Snapshot().Running != true) { TerminalStatus.Text = "请先启动工程终端。"; return; }
        var privateInput = PrivateCheck.IsChecked == true;
        var text = SingleLine(privateInput ? SecretInput.Password : CommandInput.Text);
        // 输入框不自动执行粘贴的多行；将命令提交和粘贴明确分开。
        if (!privateInput && text.Length > 0) { history.Add(text); if (history.Count > 100) history.RemoveAt(0); historyIndex = history.Count; }
        if (privateInput) SecretInput.Clear(); else CommandInput.Clear();
        await SendRawAsync(text + "\r");
    }
    private async void Command_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SubmitAsync(); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && (sender == SecretInput || CommandInput.SelectionLength == 0))
        { e.Handled = true; await SendRawAsync("\x03"); }
        else if (sender == CommandInput && e.Key is Key.Up or Key.Down && history.Count > 0)
        { e.Handled = true; historyIndex = Math.Clamp(historyIndex + (e.Key == Key.Up ? -1 : 1), 0, history.Count); CommandInput.Text = historyIndex < history.Count ? history[historyIndex] : ""; CommandInput.CaretIndex = CommandInput.Text.Length; }
    }
    private void Private_Changed(object sender, RoutedEventArgs e)
    {
        if (CommandInput is null || SecretInput is null) return;
        var secret = PrivateCheck.IsChecked == true;
        CommandInput.Visibility = secret ? Visibility.Collapsed : Visibility.Visible; SecretInput.Visibility = secret ? Visibility.Visible : Visibility.Collapsed;
        SecretInput.Clear(); if (secret) SecretInput.Focus(); else CommandInput.Focus();
    }
    private async void Output_KeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.C && OutputEditor.SelectionLength > 0) return;
        if (control && e.Key == Key.A) return;
        if (control && e.Key == Key.V) { e.Handled = true; CommandInput.Text = SingleLine(Clipboard.GetText()); CommandInput.Focus(); return; }
        var text = e.Key switch
        {
            Key.Enter => "\r", Key.Back => "\x7f", Key.Tab => "\t", Key.Escape => "\x1b", Key.Up => "\x1b[A", Key.Down => "\x1b[B", Key.Right => "\x1b[C", Key.Left => "\x1b[D", Key.Home => "\x1b[H", Key.End => "\x1b[F", Key.Delete => "\x1b[3~", _ => null
        };
        if (control && e.Key is >= Key.A and <= Key.Z) text = ((char)(e.Key - Key.A + 1)).ToString();
        if (text is null) return; e.Handled = true; await SendRawAsync(text);
    }
    private async Task SendRawAsync(string text)
    { try { if (service is not null) await service.SendAsync(text); error = null; FollowCheck.IsChecked = true; } catch (Exception ex) { Error(ex); } }
    private static string SingleLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    private void Refresh()
    {
        // 切换工程后，旧会话仍可保留在服务的只读快照中；新终端启动前不再显示它。
        if (!IsVisible || service is null || project is null || !hasSession) return;
        try
        {
            var size = OutputEditor.FontSize;
            service.Resize((int)Math.Max(40, (OutputEditor.ActualWidth - 30) / (size * .61)), (int)Math.Max(6, (OutputEditor.ActualHeight - 12) / (size * 1.4)));
            var snapshot = service.Snapshot(); var display = snapshot.Screen.Display;
            StartButton.IsEnabled = !snapshot.Running && !busy; InterruptButton.IsEnabled = snapshot.Running;
            TerminalStatus.Text = error ?? snapshot.Status + " · Enter 输入 · Ctrl+C 中断 · 上下键历史";
            if (display.Version == version) return;
            version = display.Version;
            var offset = OutputEditor.VerticalOffset;
            var selectionStart = OutputEditor.SelectionStart; var selectionLength = OutputEditor.SelectionLength;
            colors.Spans = display.Spans; OutputEditor.Text = display.Text;
            if (FollowCheck.IsChecked == true && snapshot.Screen.CursorOffset >= 0)
            {
                OutputEditor.CaretOffset = Math.Min(OutputEditor.Document.TextLength, snapshot.Screen.CursorOffset);
                OutputEditor.ScrollToLine(OutputEditor.Document.GetLineByOffset(OutputEditor.CaretOffset).LineNumber);
            }
            else OutputEditor.ScrollToVerticalOffset(offset);
            if (selectionLength > 0)
            {
                selectionStart = Math.Min(selectionStart, OutputEditor.Document.TextLength);
                OutputEditor.Select(selectionStart, Math.Min(selectionLength, OutputEditor.Document.TextLength - selectionStart));
            }
        }
        catch (Exception ex) { Error(ex); }
    }
    public bool HandleMouseWheel(DependencyObject? target, int delta, bool control)
    {
        for (var current = target; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
            if (current == OutputEditor)
            {
                if (control) OutputEditor.FontSize = Math.Clamp(OutputEditor.FontSize + Math.Sign(delta), 10, 26);
                else { FollowCheck.IsChecked = false; OutputEditor.ScrollToVerticalOffset(Math.Max(0, OutputEditor.VerticalOffset - delta / 120.0 * 3 * OutputEditor.FontSize * 1.4)); }
                return true;
            }
        return false;
    }
    private void Error(Exception ex) { error = ex.GetBaseException().Message; TerminalStatus.Text = error; TerminalStatus.ToolTip = ex.ToString(); }
    public async Task ShutdownAsync() { timer.Stop(); if (service is not null) await service.StopAsync(); }
}

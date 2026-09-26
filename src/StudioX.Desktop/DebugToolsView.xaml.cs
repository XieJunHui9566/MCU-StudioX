namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Engine.Debugging;

public sealed record BreakpointRow(string Id, string File, int Line, bool Enabled, string Status, string Kind, string Rule, int HitCount, string Details);
public partial class DebugToolsView : UserControl
{
    private DebugSnapshot? memorySnapshot;
    public DebugToolsView()
    {
        InitializeComponent(); RtosView.ReadRequested += RequestFreeRtos;
    }
    public event Action<int>? FrameSelected;
    public event Action<string, bool>? WatchChanged;
    public event Action<string, bool?>? BreakpointChanged;
    public event Action<string>? BreakpointSettings;
    public event Action<SourceLocation>? Navigate;
    public event Action<string>? MemoryRequested;
    public void Refresh(DebugSnapshot snapshot, IReadOnlyList<SourceBreakpoint> points, IReadOnlyList<string> expressions, DebugState state, bool hardware = false)
    {
        BreakpointLogHint.Text = hardware ? "日志断点输出 · 实机数据" : "日志断点输出 · 模拟数据";
        Frames.ItemsSource = snapshot.Frames;
        Frames.SelectedItem = snapshot.Frames.FirstOrDefault(f => f.Level == snapshot.SelectedFrame);
        Locals.ItemsSource = snapshot.Locals;
        Watches.ItemsSource = expressions.Select(w => snapshot.Watches.FirstOrDefault(v => v.Name == w) ?? new DebugVariable(w, "暂停后读取")).ToArray();
        Locals.Opacity = Watches.Opacity = Frames.Opacity = state == DebugState.Running ? .55 : 1;
        var selected = (Breakpoints.SelectedItem as BreakpointRow)?.Id;
        var rows = points.Select(b => new BreakpointRow(b.Id, b.BoundLocation?.File ?? b.File, b.BoundLocation?.Line ?? b.Line, b.Enabled,
            !b.Enabled ? "已禁用" : b.Message ?? "等待调试绑定", b.Kind, b.Rule, b.HitCount,
            $"请求 {b.File}:{b.Line}" + (b.BoundLocation is { } location ? $"\n实际 {location.File}:{location.Line}" : "") +
            $"\n{b.Rule}\n已到达 {b.HitCount} 次，剩余跳过 {b.IgnoreRemaining} 次" +
            (b.LogMessage is null ? "" : "\n日志：" + b.LogMessage) + "\n" + b.Message)).ToArray();
        Breakpoints.ItemsSource = rows; Breakpoints.SelectedItem = rows.FirstOrDefault(b => b.Id == selected);
        EnableBreakpoint.IsEnabled = DeleteBreakpoint.IsEnabled = state is DebugState.Disconnected or DebugState.Stopped or DebugState.Faulted;
        EditBreakpoint.IsEnabled = EnableBreakpoint.IsEnabled;
        Frames.IsEnabled = ReadMemory.IsEnabled = state == DebugState.Stopped;
        if (state != DebugState.Stopped) Memory.Text = state == DebugState.Running ? "目标运行中，暂停后重新读取。" : "暂停后读取内存。";
        else if (!ReferenceEquals(memorySnapshot, snapshot)) Memory.Text = "已暂停，输入地址后读取内存。";
        memorySnapshot = snapshot;
        RefreshDisassembly(snapshot, state);
        RefreshFreeRtos(snapshot, state);
    }
    public void AppendOutput(string text)
    {
        Output.AppendText(text + "\n"); if (Output.Text.Length > 160000) Output.Text = "[较早调试输出已截断]\n" + Output.Text[^120000..]; Output.ScrollToEnd();
    }
    public void SetMemory(uint address, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Memory.Text = string.Join('\n', Enumerable.Range(0, (bytes.Length + 15) / 16).Select(row =>
            $"0x{address + row * 16:x8}  " + string.Join(' ', bytes.Skip(row * 16).Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))));
    }
    public void ShowBreakpoints() => DetailTabs.SelectedIndex = 2;
    public void ShowLocals() => DetailTabs.SelectedIndex = 1;
    public void AppendBreakpointLog(string text)
    {
        BreakpointOutput.AppendText(text + "\n");
        if (BreakpointOutput.Text.Length > 80000) BreakpointOutput.Text = "[较早断点日志已截断]\n" + BreakpointOutput.Text[^60000..];
        BreakpointOutput.ScrollToEnd();
    }
    public void ClearBreakpointLog() => BreakpointOutput.Clear();
    public void ShowBreakpointLog() => DetailTabs.SelectedIndex = 4;
    private void ClearBreakpointLog_Click(object sender, RoutedEventArgs e) => ClearBreakpointLog();
    private void EditBreakpoint_Click(object sender, RoutedEventArgs e) { if (Breakpoints.SelectedItem is BreakpointRow b) BreakpointSettings?.Invoke(b.Id); }
    private void Frame_DoubleClick(object sender, MouseButtonEventArgs e) { if (Frames.SelectedItem is DebugFrame frame) FrameSelected?.Invoke(frame.Level); }
    private void Breakpoint_DoubleClick(object sender, MouseButtonEventArgs e) { if (Breakpoints.SelectedItem is BreakpointRow b) Navigate?.Invoke(new(b.File, b.Line)); }
    private void AddWatch_Click(object sender, RoutedEventArgs e) => WatchChanged?.Invoke(WatchExpression.Text.Trim(), false);
    private void Watch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddWatch_Click(sender, e); e.Handled = true; } }
    private void RemoveWatch_Click(object sender, RoutedEventArgs e) { if (Watches.SelectedItem is DebugVariable variable) WatchChanged?.Invoke(variable.Name, true); }
    private void EnableBreakpoint_Click(object sender, RoutedEventArgs e) { if (Breakpoints.SelectedItem is BreakpointRow b) BreakpointChanged?.Invoke(b.Id, !b.Enabled); }
    private void DeleteBreakpoint_Click(object sender, RoutedEventArgs e) { if (Breakpoints.SelectedItem is BreakpointRow b) BreakpointChanged?.Invoke(b.Id, null); }
    private void ReadMemory_Click(object sender, RoutedEventArgs e) => MemoryRequested?.Invoke(MemoryAddress.Text);
}

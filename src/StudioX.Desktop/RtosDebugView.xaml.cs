namespace StudioX.Desktop;

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Engine.Debugging;

public partial class RtosDebugView : UserControl
{
    private DebugState state;
    private bool busy, hasSnapshot;
    private string? project;
    private readonly ObservableCollection<string> symbols = [];
    public event Action? ReadRequested;
    public event Action? CancelRequested;
    public RtosDebugView()
    {
        InitializeComponent(); ObservedSymbols.ItemsSource = symbols; UpdateControls();
    }
    internal bool RefreshOnPause => AutoRefresh.IsChecked == true;
    internal bool IsReading => busy;
    internal bool CanRead => RefreshButton.IsEnabled;
    internal int TaskCount => TasksGrid.Items.Count;
    internal int ObjectCount => ObjectsGrid.Items.Count;
    internal bool ObjectColumnsReadable => ObjectsGrid.Columns.Skip(1).All(column => column.ActualWidth >= column.Width.Value - 1);
    internal string Status => StatusText.Text;
    internal string Scheduler => SchedulerText.Text;
    internal string Diagnostics => DiagnosticsText.Text;
    internal double SnapshotOpacity => RtosTabs.Opacity;
    public IReadOnlyList<string> ObjectSymbols => symbols.ToArray();
    public void SetProject(string? directory)
    {
        if (string.Equals(directory, project, StringComparison.OrdinalIgnoreCase)) return;
        project = directory; symbols.Clear(); ObjectSymbol.Clear(); ClearSnapshot();
    }
    public void RefreshState(DebugState value, bool newPause)
    {
        state = value;
        if (value is DebugState.Starting or DebugState.Stopping or DebugState.Disconnected or DebugState.Faulted)
        {
            busy = false; ClearSnapshot(); StatusText.Text = "暂停目标后读取 FreeRTOS 内核。";
        }
        else if (value == DebugState.Running || newPause)
        {
            busy = false; RtosTabs.Opacity = hasSnapshot ? .55 : 1;
            StatusText.Text = value == DebugState.Running ? "目标运行中 · 上次暂停快照，暂停后才能读取。" :
                hasSnapshot ? "已暂停 · 上次读取结果，等待刷新。" : "已暂停 · 可以读取 FreeRTOS 内核。";
        }
        UpdateControls();
    }
    public void SetLoading()
    {
        busy = true; RtosTabs.Opacity = hasSnapshot ? .55 : 1;
        CancelButton.IsEnabled = true;
        StatusText.Text = "正在读取任务链表、堆和同步对象…"; UpdateControls();
    }
    public void SetCancelPending()
    {
        if (!busy) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消 · 当前只读命令返回后停止，不中断调试连接。";
    }
    public void SetSnapshot(FreeRtosSnapshot snapshot, bool hardware, bool sample = false)
    {
        busy = false; hasSnapshot = true; RtosTabs.Opacity = 1;
        var previousTask = (TasksGrid.SelectedItem as RtosTaskRow)?.Data.Address;
        var taskRows = snapshot.Tasks.Select(task => new RtosTaskRow(task)).ToArray();
        TasksGrid.ItemsSource = taskRows;
        TasksGrid.SelectedItem = taskRows.FirstOrDefault(task => task.Data.Address == previousTask) ?? taskRows.FirstOrDefault(task => task.IsCurrent) ?? taskRows.FirstOrDefault();
        ObjectsGrid.ItemsSource = snapshot.Objects.Select(item => new RtosObjectRow(item, snapshot.Tasks)).ToArray();
        ObjectsGrid.SelectedIndex = snapshot.Objects.Count > 0 ? 0 : -1;
        var source = sample ? "离线界面样例 · 非运行内核" : hardware ? "GDB 目标暂停快照" : "离线模拟会话 · 非实机";
        StatusText.Text = snapshot.IsAvailable ? $"{source} · {DateTime.Now:HH:mm:ss}（主机） · {snapshot.Tasks.Count} 个任务 / {snapshot.Objects.Count} 个对象" :
            $"{source} · 未找到可读取的 FreeRTOS 内核；查看读取诊断。";
        var current = snapshot.Tasks.FirstOrDefault(task => task.Address == snapshot.CurrentTaskAddress);
        var running = snapshot.SchedulerRunning is { } enabled ? enabled ? "已启动" : "未启动" : "—";
        SchedulerText.Text = $"调度器 {running}   挂起计数 {RtosDisplay.Number(snapshot.SchedulerSuspended)}   Tick {RtosDisplay.Number(snapshot.TickCount)}   " +
            $"内核任务数 {RtosDisplay.Number(snapshot.ReportedTaskCount)}   当前 {current?.Name ?? RtosDisplay.Address(snapshot.CurrentTaskAddress)}";
        DiagnosticsText.Text = string.Join(Environment.NewLine + Environment.NewLine, snapshot.Diagnostics);
        if (DiagnosticsText.Text.Length == 0) DiagnosticsText.Text = "读取完成，没有返回诊断。";
        SetHeap(snapshot.Heap);
        if (!snapshot.IsAvailable) { TaskDetails.Text = "FreeRTOS 任务不可用。"; ObjectDetails.Text = "FreeRTOS 对象不可用。"; }
        UpdateControls();
    }
    public void SetError(string message)
    {
        busy = false; ClearSnapshot(); StatusText.Text = "RTOS 读取失败：" + message;
        DiagnosticsText.Text = message; UpdateControls();
    }
    public void SetCanceled()
    {
        busy = false;
        StatusText.Text = hasSnapshot ? "已取消读取 · 保留上次暂停快照。" : "已取消读取。";
        RtosTabs.Opacity = hasSnapshot ? .55 : 1; UpdateControls();
    }
    private void ClearSnapshot()
    {
        hasSnapshot = false; TasksGrid.ItemsSource = ObjectsGrid.ItemsSource = HeapItems.ItemsSource = null;
        HeapUsage.Visibility = Visibility.Collapsed; HeapCaption.Text = "未读取堆信息。";
        TaskDetails.Text = "选择任务查看 TCB 和保存的栈指针。"; ObjectDetails.Text = "选择对象查看地址和互斥锁持有者。";
        SchedulerText.Text = "调度器 / Tick / 当前任务：—"; DiagnosticsText.Text = "尚未读取。"; RtosTabs.Opacity = 1;
    }
    private void SetHeap(FreeRtosHeap? heap)
    {
        if (heap is null)
        {
            HeapItems.ItemsSource = null; HeapUsage.Visibility = Visibility.Collapsed;
            HeapCaption.Text = "目标未提供可识别的 FreeRTOS 堆统计；查看读取诊断。"; return;
        }
        var rows = new List<RtosHeapRow>
        {
            new("堆保留范围 (B)", RtosDisplay.Number(heap.TotalBytes)),
            new("当前空闲 (B)", RtosDisplay.Number(heap.FreeBytes)),
            new("历史最小空闲 (B)", RtosDisplay.Number(heap.MinimumEverFreeBytes)),
            new("最大空闲块（含块头，B）", RtosDisplay.Number(heap.LargestFreeBlockBytes)),
            new("空闲块数量", RtosDisplay.Number(heap.FreeBlockCount)),
            new("成功分配次数", RtosDisplay.Number(heap.AllocationCount)),
            new("成功释放次数", RtosDisplay.Number(heap.FreeCount))
        };
        if (heap.LargestFreeBlockBytes is { } largest && heap.FreeBytes is > 0)
            rows.Add(new("空闲块分散率 · 1 − 最大块 / 总空闲", $"{Math.Clamp(1 - (double)largest / heap.FreeBytes.Value, 0, 1):P1}"));
        HeapItems.ItemsSource = rows; HeapCaption.Text = heap.Kind + " · — 表示该分配器或 ELF 未提供字段。";
        if (heap.TotalBytes is > 0 && heap.FreeBytes is { } free && free <= heap.TotalBytes.Value)
        {
            HeapUsage.Value = 100 * (1 - (double)free / heap.TotalBytes.Value); HeapUsage.Visibility = Visibility.Visible;
            HeapCaption.Text += $" 保留范围占用 {HeapUsage.Value:F1}%（含管理开销）。";
        }
        else HeapUsage.Visibility = Visibility.Collapsed;
    }
    private void UpdateControls()
    {
        var enabled = state == DebugState.Stopped && !busy;
        RefreshButton.IsEnabled = ObserveButton.IsEnabled = ObjectSymbol.IsEnabled = RemoveObjectButton.IsEnabled = enabled;
        AutoRefresh.IsEnabled = state == DebugState.Stopped;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => ReadRequested?.Invoke();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void Task_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskDetails is not null) TaskDetails.Text = (TasksGrid.SelectedItem as RtosTaskRow)?.Details ?? "选择任务查看 TCB 和保存的栈指针。";
    }
    private void Object_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObjectDetails is not null) ObjectDetails.Text = (ObjectsGrid.SelectedItem as RtosObjectRow)?.Details ?? "选择对象查看地址和互斥锁持有者。";
    }
    private void Observe_Click(object sender, RoutedEventArgs e) => ObserveSymbol(ObjectSymbol.Text);
    internal void ObserveSymbol(string value)
    {
        var name = value.Trim();
        if (!Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\z", RegexOptions.CultureInvariant) || name.Length > 256)
        {
            StatusText.Text = "请输入全局句柄变量名或点分隔成员名，例如 ledQueue；不支持函数或赋值。"; return;
        }
        if (!symbols.Contains(name)) symbols.Add(name);
        ObservedSymbols.SelectedItem = name; ObjectSymbol.Clear(); ReadRequested?.Invoke();
    }
    private void ObjectSymbol_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ObserveButton.IsEnabled) { Observe_Click(sender, e); e.Handled = true; }
    }
    private void RemoveObject_Click(object sender, RoutedEventArgs e)
    {
        if (ObservedSymbols.SelectedItem is not string name) return;
        symbols.Remove(name); ReadRequested?.Invoke();
    }
    internal void ShowPage(int index) => RtosTabs.SelectedIndex = index;
}

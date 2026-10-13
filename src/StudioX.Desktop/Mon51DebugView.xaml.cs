namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application.StcDebugging;
using StudioX.Engine.Debugging;

public partial class Mon51DebugView : UserControl
{
    private DebugSnapshot? lastSnapshot;
    public Mon51DebugView()
    {
        InitializeComponent();
        Space.ItemsSource = new[] { new SpaceChoice(Mon51MemorySpace.DataSfr, "DATA / SFR（直接）"), new SpaceChoice(Mon51MemorySpace.Idata, "IDATA（间接）"), new SpaceChoice(Mon51MemorySpace.Xdata, "XDATA"), new SpaceChoice(Mon51MemorySpace.Code, "CODE") };
        Space.DisplayMemberPath = "Label";
        Space.SelectedIndex = 0;
        EditRegister.ItemsSource = new[] { "PC", "A", "B", "DPTR", "PSW", "SP", "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "CY", "AC", "OV", "寄存器组" };
        EditRegister.SelectedIndex = 1;
        SourceToolsView.ConfigureMon51();
    }
    public event Action<Mon51MemorySpace, ushort, int>? MemoryRequested;
    public event Action<ushort>? AddBreakpointRequested;
    public event Action<ushort>? RemoveBreakpointRequested;
    public event Action<ushort>? PcRequested;
    public event Action? SymbolsRequested;
    public event Action? InstructionRequested;
    public event Action<bool>? SourceStepChanged;
    public event Action<Mon51MemorySpace, ushort, byte[]>? MemoryWriteRequested;
    public event Action<string, uint>? RegisterWriteRequested;
    public event Action<string, long>? VariableWriteRequested;
    public DebugToolsView SourceTools => SourceToolsView;
    public void ShowSourceTools() => Tabs.SelectedItem = SourceTab;

    public void Refresh(Mon51DebugSession session, bool busy)
    {
        var stopped = session.State == DebugState.Stopped;
        SessionHint.Text = (session.IsSimulated ? "离线协议预览 · " : "实机 · ") + $"Mon51 {session.Version} · {session.Port} · {session.SymbolsStatus}";
        InstructionHint.Text = $"PC 0x{session.Pc:X4}  CODE: {session.InstructionBytes}" + (stopped ? "" : "  （上次暂停快照）");
        MemoryControls.IsEnabled = BreakpointControls.IsEnabled = stopped && !busy;
        SessionControls.IsEnabled = EditControls.IsEnabled = stopped && !busy;
        SourceStep.IsEnabled = SetVariableButton.IsEnabled = session.HasSymbols;
        SourceToolsView.Refresh(session.Snapshot, session.SourceBreakpoints, session.Watches, session.State, !session.IsSimulated);
        SourceToolsView.SetMon51StackHint(session.StackStatus);
        var selected = (Points.SelectedItem as Mon51Breakpoint)?.Address;
        Points.ItemsSource = session.Breakpoints;
        Points.SelectedItem = session.Breakpoints.FirstOrDefault(p => p.Address == selected);
        if (!ReferenceEquals(lastSnapshot, session.Snapshot))
        {
            Memory.Text = stopped ? "目标状态已更新，请重新读取内存。" : "目标运行中，暂停后重新读取。";
            lastSnapshot = session.Snapshot;
        }
        if (!stopped)
        {
            Memory.Text = "目标运行中或连接未就绪，暂停后重新读取。";
        }
    }

    public void SetMemory(Mon51MemorySpace space, ushort address, byte[] bytes)
    {
        Memory.Text = $"{space} · 宿主读取时间 {DateTimeOffset.Now:HH:mm:ss.fff}\n" + string.Join('\n', Enumerable.Range(0, (bytes.Length + 15) / 16).Select(row =>
            $"0x{address + row * 16:X4}  " + string.Join(' ', bytes.Skip(row * 16).Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))));
    }
    public void AppendOutput(string text)
    {
        Output.AppendText(text + "\n");
        if (Output.Text.Length > 160000)
        {
            Output.Text = "[较早日志已截断；完整日志保存在会话文件]\n" + Output.Text[^120000..];
        }
        Output.ScrollToEnd();
    }
    private bool ParseAddress(TextBox box, out ushort address)
    {
        if (ushort.TryParse(box.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
        {
            return true;
        }
        ShowError("请输入 0000–FFFF 范围内的十六进制地址。");
        return false;
    }
    private void ShowError(string text)
    {
        AppendOutput(text);
        Tabs.SelectedIndex = 2;
    }
    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (Space.SelectedItem is not SpaceChoice choice || !ParseAddress(Address, out var address))
        {
            return;
        }
        if (!int.TryParse(Count.Text, out var count) || count is < 1 or > 128)
        {
            ShowError("字节数必须为 1–128。");
            return;
        }
        MemoryRequested?.Invoke(choice.Space, address, count);
    }
    private void Space_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RangeHint is null || Space.SelectedItem is not SpaceChoice choice)
        {
            return;
        }
        RangeHint.Text = choice.Space switch
        {
            Mon51MemorySpace.Code => "0000–DBFF；跳板 DBFD–DBFF",
            Mon51MemorySpace.Xdata => "0000–03FF；避开监控保留区",
            _ => "00–FF；SFR 仅支持直接寻址"
        };
        Memory.Text = "存储区已切换，请重新读取。";
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (ParseAddress(BreakpointAddress, out var address))
        {
            AddBreakpointRequested?.Invoke(address);
        }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Points.SelectedItem is Mon51Breakpoint point)
        {
            RemoveBreakpointRequested?.Invoke(point.Address);
        }
    }
    private void Pc_Click(object sender, RoutedEventArgs e)
    {
        if (ParseAddress(PcAddress, out var address))
        {
            PcRequested?.Invoke(address);
        }
    }
    private void Symbols_Click(object sender, RoutedEventArgs e) => SymbolsRequested?.Invoke();
    private void Instruction_Click(object sender, RoutedEventArgs e) => InstructionRequested?.Invoke();
    private void StepMode_Changed(object sender, RoutedEventArgs e) => SourceStepChanged?.Invoke(SourceStep.IsChecked == true);
    private void Write_Click(object sender, RoutedEventArgs e)
    {
        if (Space.SelectedItem is not SpaceChoice choice || !ParseAddress(Address, out var address))
        {
            return;
        }
        try
        {
            var bytes = Convert.FromHexString(string.Concat(WriteBytes.Text.Where(c => !char.IsWhiteSpace(c))));
            if (bytes.Length is < 1 or > 128)
            {
                ShowError("写入字节数须为 1–128。");
                return;
            }
            MemoryWriteRequested?.Invoke(choice.Space, address, bytes);
        }
        catch (FormatException) { ShowError("请输入完整的十六进制字节，例如 01 02 FF。"); }
    }
    private void Register_Click(object sender, RoutedEventArgs e)
    {
        if (EditRegister.SelectedItem is not string register)
        {
            return;
        }
        var text = RegisterValue.Text.Trim();
        if (!uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            ShowError("寄存器值请输入十进制或 0x 十六进制整数。");
            return;
        }
        RegisterWriteRequested?.Invoke(register, value);
    }
    private void Variable_Click(object sender, RoutedEventArgs e)
    {
        var text = VariableValue.Text.Trim();
        if (!long.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            ShowError("变量值请输入十进制或 0x 十六进制整数。");
            return;
        }
        VariableWriteRequested?.Invoke(EditVariable.Text.Trim(), value);
    }
    private sealed record SpaceChoice(Mon51MemorySpace Space, string Label);
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Engine.Debugging;

public partial class DebugToolsView
{
    private DebugState disassemblyState;
    private DebugSnapshot? disassemblySnapshot;
    private DebugDisassembly? disassembly;
    private bool disassemblyBusy;
    public event Action<string?>? DisassemblyRequested;

    internal int DisassemblyInstructionCount => Instructions.Items.Count;
    internal uint? DisassemblyCurrentAddress => Instructions.Items.Cast<DebugDisassemblyRow>().FirstOrDefault(row => row.IsCurrent)?.Data.Address;
    internal bool CanReadDisassembly => ReadDisassembly.IsEnabled;
    internal string DisassemblyStatus => DisassemblyHint.Text;
    public void ShowDisassembly() => DetailTabs.SelectedItem = DisassemblyTab;

    private void RefreshDisassembly(DebugSnapshot snapshot, DebugState state)
    {
        var changed = !ReferenceEquals(disassemblySnapshot, snapshot);
        disassemblySnapshot = snapshot;
        disassemblyState = state;
        if (state is DebugState.Disconnected or DebugState.Faulted or DebugState.Starting or DebugState.Stopping)
        {
            disassembly = null; Instructions.ItemsSource = null; disassemblyBusy = false;
            DisassemblyHint.Text = "暂停后读取指令。";
        }
        else if (state == DebugState.Running || changed)
        {
            disassemblyBusy = false;
            // 恢复运行后原 PC 已失效；保留可浏览的旧指令，但不再标成当前执行位置。
            if (disassembly is { } previous) Instructions.ItemsSource = previous.Instructions.Select(item => new DebugDisassemblyRow(item, false)).ToArray();
            Instructions.Opacity = .55;
            DisassemblyHint.Text = state == DebugState.Running ? "目标运行中 · 上次暂停快照，暂停后才能读取。" : "已暂停 · 显示上次读取结果。";
        }
        UpdateDisassemblyControls();
        if (state == DebugState.Stopped && changed && DisassemblyTab.IsSelected && FollowDisassembly.IsChecked == true)
            DisassemblyRequested?.Invoke(null);
    }

    public void SetDisassemblyLoading()
    {
        disassemblyBusy = true; UpdateDisassemblyControls();
        DisassemblyHint.Text = "正在读取 GDB 反汇编…";
    }

    public void SetDisassembly(DebugDisassembly result, bool hardware)
    {
        disassemblyBusy = false; disassembly = result;
        var rows = result.Instructions.Select(item => new DebugDisassemblyRow(item, result.ProgramCounter == item.Address)).ToArray();
        Instructions.ItemsSource = rows; Instructions.Opacity = 1;
        DisassemblyAddress.Text = $"0x{result.StartAddress:x8}";
        var focus = rows.FirstOrDefault(row => row.Data.Address == result.FocusAddress);
        Instructions.SelectedItem = focus;
        if (focus is not null) Instructions.ScrollIntoView(focus);
        var source = hardware ? "GDB 目标指令" : "离线模拟指令 · 非 ELF / Flash 实际内容";
        DisassemblyHint.Text = $"{source} · 0x{result.StartAddress:x8}–0x{result.EndAddress:x8}（终点不含） · " +
            (rows.Length == 0 ? "该范围没有返回可用指令。" : $"{rows.Length} 条" + (result.ProgramCounter is { } pc ? $" · PC 0x{pc:x8}" : " · PC 不可用"));
        UpdateDisassemblyControls();
    }

    public void SetDisassemblyError(string message)
    {
        disassemblyBusy = false; disassembly = null; Instructions.ItemsSource = null;
        DisassemblyHint.Text = "读取失败：" + message; UpdateDisassemblyControls();
    }

    private void UpdateDisassemblyControls()
    {
        var enabled = disassemblyState == DebugState.Stopped && !disassemblyBusy;
        ReadDisassembly.IsEnabled = DisassemblyPc.IsEnabled = DisassemblyAddress.IsEnabled = enabled;
        DisassemblyFrame.IsEnabled = enabled && Frames.SelectedItem is DebugFrame;
        FollowDisassembly.IsEnabled = disassemblyState == DebugState.Stopped;
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DetailTabs) && RtosView is not null && RtosTab.IsSelected) RequestFreeRtos();
        if (!ReferenceEquals(e.Source, DetailTabs) || Instructions is null || !DisassemblyTab.IsSelected || disassemblyState != DebugState.Stopped || disassemblyBusy) return;
        DisassemblyRequested?.Invoke(FollowDisassembly.IsChecked == true ? null : DisassemblyAddress.Text);
    }
    private void ReadDisassembly_Click(object sender, RoutedEventArgs e)
    {
        ReadDisassemblyAt(DisassemblyAddress.Text);
    }
    internal void ReadDisassemblyAt(string address)
    {
        FollowDisassembly.IsChecked = false; DisassemblyRequested?.Invoke(address);
    }
    private void DisassemblyAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ReadDisassembly.IsEnabled) { ReadDisassembly_Click(sender, e); e.Handled = true; }
    }
    private void DisassemblyPc_Click(object sender, RoutedEventArgs e)
    {
        FollowCurrentDisassembly();
    }
    internal void FollowCurrentDisassembly()
    {
        FollowDisassembly.IsChecked = true; DisassemblyRequested?.Invoke(null);
    }
    private void DisassemblyFrame_Click(object sender, RoutedEventArgs e)
    {
        if (Frames.SelectedItem is not DebugFrame frame) return;
        ReadDisassemblyAt(frame.Address);
    }
    private void FollowDisassembly_Click(object sender, RoutedEventArgs e)
    {
        if (FollowDisassembly.IsChecked == true && disassemblyState == DebugState.Stopped) DisassemblyRequested?.Invoke(null);
    }
}

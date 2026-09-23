namespace StudioX.Desktop;

using System.Runtime.InteropServices;
using System.Windows.Interop;

public partial class MainWindow
{
    private HwndSource? windowSource;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowBoundsMessage);
    }

    private static nint WindowBoundsMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        if (message != getMinMaxInfo || !WindowMonitor.TryGetBounds(window, out var monitor, out var work)) return 0;
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // 自绘边框的客户区覆盖整个窗口；最大化必须使用当前屏幕工作区，否则底部落入任务栏后方。
        // 消息和屏幕矩形均为物理像素，不混入 WPF DIP；保留系统/WPF 的最小尺寸及拖动尺寸约束。
        bounds.MaxPosition = new NativePoint { X = (int)(work.Left - monitor.Left), Y = (int)(work.Top - monitor.Top) };
        bounds.MaxSize = new NativePoint { X = (int)work.Width, Y = (int)work.Height };
        Marshal.StructureToPtr(bounds, lParam, false);
        // 继续交给 WPF 缓存最大化尺寸并应用 MinWidth/MinHeight；吞掉消息会破坏普通窗口缩放约束。
        handled = false;
        return 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelGitHubProfileRefresh();
        if (windowSource is { IsDisposed: false })
        {
            windowSource.RemoveHook(WindowBoundsMessage);
        }
        windowSource = null;
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
}

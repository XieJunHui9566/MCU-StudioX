namespace StudioX.Desktop;

using System.Runtime.InteropServices;
using System.Windows;

/// <summary>窗口所在屏幕的物理像素边界；工作区排除任务栏，不能与 WPF 的逻辑尺寸混用。</summary>
internal static class WindowMonitor
{
    internal static bool TryGetBounds(nint window, out Rect monitor, out Rect work)
    {
        monitor = work = Rect.Empty;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var handle = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST：兼容副屏和负坐标。
        if (handle == 0 || !GetMonitorInfo(handle, ref info)) return false;
        monitor = info.Monitor.ToRect(); work = info.Work.ToRect();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

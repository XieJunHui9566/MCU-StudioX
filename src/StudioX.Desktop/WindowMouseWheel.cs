namespace StudioX.Desktop;

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

/// <summary>统一所有窗口和页面的滚轮路由，以鼠标命中区域为准，不要求该区域持有键盘焦点。</summary>
internal static class WindowMouseWheel
{
    private static readonly ConditionalWeakTable<Window, NativeHook> hooks = new();
    private static readonly ConditionalWeakTable<ScrollViewer, WheelRemainder> remainders = new();
    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PreviewWheel));
        // 下拉列表等 Popup 有独立 HWND，补上 ScrollViewer 的路由入口。
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PreviewWheel));
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window) && !hooks.TryGetValue(window, out _))
            hooks.Add(window, new NativeHook(window));
    }

    private static void PreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement { IsEnabled: true }) return;
        var hit = Mouse.DirectlyOver as DependencyObject ?? e.OriginalSource as DependencyObject;
        var window = sender as Window ?? (hit is not null ? Window.GetWindow(hit) : null);
        if (Route(window, hit, e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) e.Handled = true;
    }

    private static bool Route(Window? window, DependencyObject? hit, int delta, bool control)
    {
        if (delta == 0) return false;
        if (window is MainWindow main && main.HandleTextMouseWheel(hit, delta, control)) return true;
        return !control && ScrollAtPointer(hit, delta);
    }

    private static bool ScrollAtPointer(DependencyObject? hit, int delta)
    {
        // 从最内层向外查找：内层没有可滚动内容或已经到边界时，允许滚动外层页面。
        for (var item = hit; item is not null; item = Parent(item))
        {
            if (item is not ScrollViewer { IsVisible: true, IsEnabled: true } viewer) continue;
            var horizontal = viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && viewer.ScrollableWidth > 0;
            var offset = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
            var maximum = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
            if (maximum <= 0 || (delta > 0 ? offset <= 0 : offset >= maximum - 0.01)) continue;

            var lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return true;
            var remainder = remainders.GetValue(viewer, static _ => new WheelRemainder());
            if (remainder.Horizontal != horizontal) remainder.Delta = 0;
            remainder.Horizontal = horizontal;
            remainder.Delta += delta;
            var steps = remainder.Delta / Mouse.MouseWheelDeltaForOneLine;
            remainder.Delta %= Mouse.MouseWheelDeltaForOneLine;
            // 使用 ScrollViewer 的行/页命令，兼容 AvalonEdit 像素滚动和树/列表的逻辑滚动。
            var count = Math.Abs(steps) * (lines < 0 ? 1 : lines);
            for (var i = 0; i < count; ++i)
            {
                if (horizontal)
                {
                    if (lines < 0) { if (steps > 0) viewer.PageLeft(); else viewer.PageRight(); }
                    else { if (steps > 0) viewer.LineLeft(); else viewer.LineRight(); }
                }
                else if (lines < 0) { if (steps > 0) viewer.PageUp(); else viewer.PageDown(); }
                else { if (steps > 0) viewer.LineUp(); else viewer.LineDown(); }
            }
            return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject item) => item is Visual ? VisualTreeHelper.GetParent(item) :
        item is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(item);

    private sealed class WheelRemainder
    {
        public int Delta;
        public bool Horizontal;
    }

    private sealed class NativeHook
    {
        private readonly Window window;
        private readonly HwndSource? source;
        public NativeHook(Window window)
        {
            this.window = window;
            source = PresentationSource.FromVisual(window) as HwndSource;
            source?.AddHook(Message);
            window.Closed += Closed;
        }
        private nint Message(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            const int mouseWheel = 0x020A, control = 0x0008;
            if (handled || message != mouseWheel || !window.IsEnabled || !window.IsVisible) return 0;
            // lParam 是物理屏幕坐标，PointFromScreen 同时完成窗口偏移和 DPI 换算。
            var point = window.PointFromScreen(new Point(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16))));
            var hit = window.InputHitTest(point) as DependencyObject;
            handled = Route(window, hit, unchecked((short)((long)wParam >> 16)), ((long)wParam & control) != 0);
            return 0;
        }
        private void Closed(object? sender, EventArgs e)
        {
            if (source is { IsDisposed: false }) source.RemoveHook(Message);
            window.Closed -= Closed;
            hooks.Remove(window);
        }
    }
}

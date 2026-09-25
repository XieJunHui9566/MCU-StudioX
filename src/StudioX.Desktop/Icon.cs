namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;

/// <summary>应用自绘的单色矢量符号，随 DPI 和主题缩放，不使用第三方品牌资产。</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("chip", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    // 小尺寸图标必须遵守布局约束，否则 WPF 仍按 18 像素绘制并裁掉右侧和底部。
    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(18, availableSize.Width), Math.Min(18, availableSize.Height));
    protected override void OnRender(DrawingContext dc)
    {
        var data = Kind switch
        {
            "folder" => "M3,5 L10,5 12,8 21,8 21,19 3,19 Z M3,9 L21,9",
            "file" => "M6,3 L14,3 19,8 19,21 6,21 Z M14,3 L14,8 19,8 M9,12 L16,12 M9,16 L15,16",
            "plus" => "M12,5 L12,19 M5,12 L19,12",
            "new-chat" => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,7 L12,17 M7,12 L17,12",
            "history" => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,6 L12,12 16,15",
            "trash" => "M4,6 L20,6 M9,6 L9,4 15,4 15,6 M6,6 L7,20 17,20 18,6 M10,10 L10,17 M14,10 L14,17",
            "save" => "M4,3 L18,3 21,6 21,21 3,21 3,3 Z M7,3 L7,10 17,10 17,3 M7,21 L7,14 17,14 17,21",
            "undo" => "M9,5 L3,10 9,15 M3,10 L14,10 C22,10 22,20 14,20",
            "redo" => "M15,5 L21,10 15,15 M21,10 L10,10 C2,10 2,20 10,20",
            "build" => "M7,4 L2,9 7,14 M17,4 L22,9 17,14 M14,3 L10,15 M4,20 L20,20",
            "play" => "M7,4 L20,12 7,20 Z",
            "pause" => "M7,5 L7,19 M17,5 L17,19",
            "step-over" => "M3,14 C3,3 21,3 21,14 M17,10 L21,14 23,9 M9,20 L15,20",
            "step-into" => "M12,3 L12,15 M7,10 L12,15 17,10 M8,21 L16,21",
            "step-out" => "M12,16 L12,4 M7,9 L12,4 17,9 M8,21 L16,21",
            "restart" => "M5,9 A8,8 0 1 1 4,16 M5,3 L5,9 11,9",
            "stop" => "M6,6 L18,6 18,18 6,18 Z",
            "debug" => "M8,8 L16,8 17,16 Q12,23 7,16 Z M9,8 L9,5 15,5 15,8 M12,9 L12,18 M3,10 L7,11 M17,11 L21,10 M3,16 L7,15 M17,15 L21,16 M6,3 L9,6 M18,3 L15,6",
            "download" => "M12,3 L12,15 M7,10 L12,15 17,10 M4,16 L4,21 20,21 20,16",
            "search" => "M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21",
            "settings" => "M12,3 L14,3 15,6 18,6 20,9 18,12 20,15 18,18 15,18 14,21 10,21 9,18 6,18 4,15 6,12 4,9 6,6 9,6 10,3 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12",
            "package" => "M3,7 L12,2 21,7 21,17 12,22 3,17 Z M3,7 L12,12 21,7 M12,12 L12,22 M7,4 L17,9",
            "wave" => "M2,13 L5,13 7,5 11,20 15,8 18,13 22,13",
            "terminal" => "M3,4 L21,4 21,20 3,20 Z M6,8 L10,12 6,16 M13,16 L18,16",
            "git-branch" => "M7,4 A2,2 0 1 1 3,4 A2,2 0 1 1 7,4 M7,20 A2,2 0 1 1 3,20 A2,2 0 1 1 7,20 M21,7 A2,2 0 1 1 17,7 A2,2 0 1 1 21,7 M5,6 L5,18 M19,9 C19,14 16,15 10,15 C7,15 5,17 5,18",
            "serial" => "M3,3 L21,3 19,12 5,12 Z M7,6 L7,8 M12,6 L12,8 M17,6 L17,8 M4,16 L18,16 M15,14 L18,16 15,18 M20,21 L6,21 M9,19 L6,21 9,23",
            "build-log" => "M5,3 L19,3 19,21 5,21 Z M8,7 L16,7 M8,11 L16,11 M8,16 L10,18 15,14",
            "memory-usage" => "M3,20 L3,13 7,13 7,20 Z M10,20 L10,8 14,8 14,20 Z M17,20 L17,3 21,3 21,20 Z",
            "home" => "M2,11 L12,3 22,11 M5,9 L5,21 10,21 10,15 14,15 14,21 19,21 19,9",
            "plug" => "M8,3 L8,8 M16,3 L16,8 M5,8 L19,8 19,11 Q19,16 12,16 Q5,16 5,11 Z M12,16 L12,21",
            "image" => "M3,4 L21,4 21,20 3,20 Z M3,16 L8,11 13,16 17,12 21,16 M17,8 A1,1 0 1 1 15,8 A1,1 0 1 1 17,8",
            "video" => "M3,5 L16,5 16,19 3,19 Z M16,10 L22,6 22,18 16,14",
            "book" => "M12,6 Q7,2 2,5 L2,20 Q7,17 12,21 Q17,17 22,20 L22,5 Q17,2 12,6 L12,21",
            "arrow" => "M5,12 L19,12 M14,7 L19,12 14,17",
            "close" => "M6,6 L18,18 M6,18 L18,6",
            "minus" => "M5,12 L19,12",
            "maximize" => "M5,5 L19,5 19,19 5,19 Z",
            "chevron" => "M8,5 L15,12 8,19",
            "info" => "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,10 L12,17 M12,6 L12,7",
            "ai" => "M12,2 L14.3,9.7 22,12 14.3,14.3 12,22 9.7,14.3 2,12 9.7,9.7 Z M19,2 L19.6,4.4 22,5 19.6,5.6 19,8 18.4,5.6 16,5 18.4,4.4 Z",
            _ => "M6,6 L18,6 18,18 6,18 Z M9,9 L15,9 15,15 9,15 Z M9,2 L9,6 M15,2 L15,6 M9,18 L9,22 M15,18 L15,22 M2,9 L6,9 M2,15 L6,15 M18,9 L22,9 M18,15 L22,15"
        };
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Kind == "play" ? Foreground : null, new Pen(Foreground, 1.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(data));
        dc.Pop();
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using StudioX.Extensions.Abstractions;

/// <summary>使用插件给出的物理坐标绘制封装；材质与交互参考 AGM，编号方向不作推断。</summary>
internal sealed class PluginMcuPackageDiagram : Canvas
{
    private const double BodySize = 360;
    private const double LeadLength = 96;
    private const double LabelSpace = 42;
    private const double Inset = LeadLength + LabelSpace + 16;
    private static readonly ControlTemplate PinTemplate = CreatePinTemplate();
    private readonly Dictionary<string, Button> buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Rect> bounds = new(StringComparer.Ordinal);

    internal PluginMcuPackageDiagram(PluginMcuConfigurationState state, Action<PluginMcuPin> select, Func<PluginMcuPin, Task>? context = null)
    {
        Width = BodySize + Inset * 2;
        Height = Width;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;
        DrawBody(state);
        foreach (var pin in state.Pins)
        {
            var x = Inset + pin.X / state.Width * BodySize;
            var y = Inset + pin.Y / state.Height * BodySize;
            var length = LeadLength + LabelSpace;
            var rect = pin.Side switch
            {
                "left" => new Rect(Inset - length, y - 12, length, 24),
                "right" => new Rect(Inset + BodySize, y - 12, length, 24),
                "top" => new Rect(x - 12, Inset - length, 24, length),
                "bottom" => new Rect(x - 12, Inset + BodySize, 24, length),
                _ => new Rect(x - 38, y - 12, 76, 24)
            };
            var button = CreatePin(pin, rect, state.SelectedPinId == pin.Id);
            button.Click += (_, _) => select(pin);
            if (context is not null)
            {
                button.PreviewMouseRightButtonUp += async (_, e) =>
                {
                    e.Handled = true;
                    await context(pin);
                };
            }
            buttons.Add(pin.Id, button);
            bounds.Add(pin.Id, rect);
            Place(this, button, rect.X, rect.Y);
        }
    }

    internal Button PinAnchor(string id) => buttons[id];
    internal Rect PinBounds(string id) => bounds[id];

    private void DrawBody(PluginMcuConfigurationState state)
    {
        var body = new Border
        {
            Width = BodySize,
            Height = BodySize,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(3),
            Background = Gradient("#383D45", "#101318", "#252A31"),
            BorderBrush = Gradient("#7A838F", "#191E26", "#626B77"),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Direction = 315, Color = Colors.Black, Opacity = 0.3 },
            IsHitTestVisible = false
        };
        Place(this, body, Inset, Inset);
        Place(this, new Border
        {
            Width = BodySize - 12,
            Height = BodySize - 12,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.7),
            BorderBrush = Brush("#3D444D"),
            Background = Gradient("#282D35", "#14181E", "#20252C"),
            IsHitTestVisible = false
        }, Inset + 6, Inset + 6);
        MaterialText("MCU", 16, "#747E8B", -82);
        MaterialText(state.Title, 20, "#C5CAD2", -48);
        MaterialText(state.PackageName, 13, "#A1A9B4", -12);
        var selected = state.Pins.FirstOrDefault(pin => pin.Id == state.SelectedPinId);
        MaterialText(selected is null ? "点击引脚选择功能" : "PIN " + selected.Position + " · " + selected.Name, 14, "#C2CAD5", 35);
        MaterialText(selected is null ? "顶视封装示意" : FunctionLabel(selected), 12, "#A1A9B4", 60);

        // PIN 1 位置来自插件，不能套用 AGM 左上起始的编号方向。
        if (state.Pins.FirstOrDefault(pin => pin.Position == "1") is { } first)
        {
            var x = Math.Clamp(Inset + first.X / state.Width * BodySize, Inset + 22, Inset + BodySize - 70);
            var y = Math.Clamp(Inset + first.Y / state.Height * BodySize, Inset + 22, Inset + BodySize - 25);
            Place(this, new Ellipse { Width = 11, Height = 11, Fill = Brush("#10151C"), Stroke = Brush("#667383"), StrokeThickness = 1, IsHitTestVisible = false }, x, y);
            Place(this, new TextBlock { Text = "PIN 1", FontSize = 10, Foreground = Brush("#8D98A7"), IsHitTestVisible = false }, x + 17, y - 2);
        }
    }

    private void MaterialText(string value, double size, string color, double offset)
    {
        Place(this, new TextBlock
        {
            Text = value,
            Width = BodySize - 48,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(color),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        }, Inset + 24, Inset + BodySize / 2 + offset);
    }

    private static Button CreatePin(PluginMcuPin pin, Rect rect, bool selected)
    {
        var vertical = pin.Side is "top" or "bottom";
        var content = new Canvas { Width = rect.Width, Height = rect.Height, IsHitTestVisible = false };
        var assigned = pin.FunctionId.Length > 0;
        var lead = new Border
        {
            Width = vertical ? 18 : pin.Side == "center" ? 76 : LeadLength,
            Height = vertical ? LeadLength : 18,
            BorderThickness = new Thickness(0.6),
            BorderBrush = Brush(pin.Locked ? "#C39A3E" : assigned ? "#2C956A" : "#666E79"),
            Background = assigned ? Gradient("#8CD8AB", "#50B47B", "#2C8A5B") : pin.Category switch
            {
                "supply" => Gradient("#E6B3B8", "#CB8993", "#A55B69"),
                "ground" => Gradient("#A6AEBC", "#798492", "#56616E"),
                "reset" or "reserved" => Gradient("#CABAD9", "#AF9AC4", "#8A75A2"),
                _ => Gradient("#C6CBD1", "#979DA6", "#747D88")
            }
        };
        var function = FunctionLabel(pin);
        lead.Child = new TextBlock
        {
            Text = pin.Name + (pin.Locked ? " [锁]" : "") + (assigned ? " · " + function : ""),
            Width = pin.Side == "center" ? 70 : LeadLength - 6,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#15202B"),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            LayoutTransform = vertical ? new RotateTransform(-90) : Transform.Identity
        };
        var number = new TextBlock { Text = pin.Position, FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Width = 26, Padding = new Thickness(0, 3, 0, 0) };
        number.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "Text");
        var plate = new Border { Width = 26, Height = 24, CornerRadius = new CornerRadius(3), Child = number };
        plate.SetResourceReference(Border.BackgroundProperty, "Panel");
        var marker = new Border { Width = vertical ? 8 : 4, Height = vertical ? 4 : 8, CornerRadius = new CornerRadius(1), Opacity = selected || assigned ? 1 : 0.45 };
        marker.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : assigned ? "RunColor" : "Muted");
        switch (pin.Side)
        {
            case "left":
                Place(content, plate, 0, 0);
                Place(content, marker, 32, 8);
                Place(content, lead, LabelSpace, 3);
                break;
            case "right":
                Place(content, lead, 0, 3);
                Place(content, marker, LeadLength + 6, 8);
                Place(content, plate, LeadLength + 12, 0);
                break;
            case "top":
                Place(content, plate, -1, 0);
                Place(content, marker, 8, 32);
                Place(content, lead, 3, LabelSpace);
                break;
            case "bottom":
                Place(content, lead, 3, 0);
                Place(content, marker, 8, LeadLength + 6);
                Place(content, plate, -1, LeadLength + 12);
                break;
            default:
                Place(content, lead, 0, 3);
                break;
        }
        var button = new Button
        {
            Width = rect.Width,
            Height = rect.Height,
            Style = null,
            Template = PinTemplate,
            Content = content,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
            Tag = pin.Id
        };
        void Outline()
        {
            if (selected || button.IsMouseOver || button.IsKeyboardFocused)
            {
                button.SetResourceReference(Control.BorderBrushProperty, "Accent");
            }
            else
            {
                button.BorderBrush = Brushes.Transparent;
            }
        }
        Outline();
        button.MouseEnter += (_, _) => Outline();
        button.MouseLeave += (_, _) => Outline();
        button.GotKeyboardFocus += (_, _) => Outline();
        button.LostKeyboardFocus += (_, _) => Outline();
        var details = "PIN " + pin.Position + " · " + pin.Name + "\n" + function + (pin.Locked ? "\n已锁定" : "") + "\n" + (pin.Functions.Length == 0 ? "当前只读" : "左键分配功能；右键命名和锁定");
        var tooltip = new ToolTip { Content = details };
        tooltip.SetResourceReference(Control.BackgroundProperty, "Surface");
        tooltip.SetResourceReference(Control.ForegroundProperty, "Text");
        button.ToolTip = tooltip;
        AutomationProperties.SetName(button, "物理引脚 " + pin.Position + " " + pin.Name);
        AutomationProperties.SetHelpText(button, details);
        return button;
    }

    internal static string FunctionLabel(PluginMcuPin pin) => pin.FunctionId.Length == 0
        ? pin.Functions.Length == 0 ? "当前只读" : "未分配"
        : pin.Label.Length > 0 ? pin.Label : pin.Functions.Single(option => option.Value == pin.FunctionId).Label;

    private static ControlTemplate CreatePinTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(content);
        var outline = new FrameworkElementFactory(typeof(Border));
        outline.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        outline.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        outline.SetValue(UIElement.IsHitTestVisibleProperty, false);
        outline.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(outline);
        return new ControlTemplate(typeof(Button)) { VisualTree = root };
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(string start, string middle, string end)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(start), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(middle), 0.45));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(end), 1));
        brush.Freeze();
        return brush;
    }

    private static void Place(Canvas canvas, UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
        canvas.Children.Add(element);
    }
}

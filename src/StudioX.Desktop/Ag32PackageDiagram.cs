namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

/// <summary>
/// 呈现 AGM 四边封装的顶视引脚示意和选择，不读取工程或调用设备。
/// </summary>
public sealed class Ag32PackageDiagram : Canvas
{
    private const double PinPitch = 26;
    private const double LeadLength = 96;
    private const double PadLength = 72;
    private const double LabelSpace = 42;
    private static readonly ControlTemplate PinTemplate = CreatePinTemplate();
    private readonly Dictionary<int, Button> pinButtons = [];
    private readonly Dictionary<int, Ag32PackagePinVisual> pinVisuals = [];
    private readonly Dictionary<int, PinParts> pinParts = [];
    private readonly TextBlock titleText = CreateMaterialText("AGM", 19, "#BCC1C8");
    private readonly TextBlock packageText = CreateMaterialText("四边封装 · 顶视示意图", 13, "#9299A3");
    private readonly TextBlock selectionText = CreateMaterialText("点击引脚查看与分配", 13, "#B1B8C1");
    private int? selectedPin;
    private int pinsPerSide = 12;
    private double bodySize = 360;
    private bool isQfn;
    private double BodyInset => PinLength + LabelSpace + 28;
    private double PinLength => isQfn ? PadLength : LeadLength;

    public Ag32PackageDiagram()
    {
        ClipToBounds = true;
        UseLayoutRounding = true;
        RenderPackageBody();
    }

    /// <summary>
    /// 设置工程核实的器件和封装名称；图形仅表示编号顺序，不代表实际机械尺寸。
    /// </summary>
    public void SetPackage(string deviceId, string packageName)
    {
        titleText.Text = deviceId;
        titleText.ToolTip = deviceId;
        packageText.Text = $"{packageName} · 顶视示意图";
        var qfn = packageName.StartsWith("QFN", StringComparison.OrdinalIgnoreCase);
        if (isQfn != qfn)
        {
            isQfn = qfn;
            RebuildPresentation();
        }
    }

    private void RenderPackageBody()
    {
        Width = bodySize + BodyInset * 2;
        Height = bodySize + BodyInset * 2;
        Children.Clear();

        // 树脂使用固定物理色；浅色主题也能辨认芯片表面。
        var body = new Border
        {
            Width = bodySize,
            Height = bodySize,
            CornerRadius = new CornerRadius(isQfn ? 6 : 13),
            BorderThickness = new Thickness(3),
            Background = Gradient(new Point(0, 0), new Point(1, 1),
                ("#383D45", 0), ("#20242A", 0.16), ("#101318", 0.52), ("#252A31", 1)),
            BorderBrush = Gradient(new Point(0, 0), new Point(1, 1),
                ("#757C86", 0), ("#353B44", 0.18), ("#11151B", 0.6), ("#626B77", 1)),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 5,
                Direction = 315,
                Color = Colors.Black,
                Opacity = 0.3
            },
            IsHitTestVisible = false
        };
        AddAt(body, BodyInset, BodyInset);

        var face = new Border
        {
            Width = bodySize - 12,
            Height = bodySize - 12,
            CornerRadius = new CornerRadius(isQfn ? 4 : 10),
            BorderThickness = new Thickness(0.7),
            BorderBrush = Brush("#3D444D"),
            Background = Gradient(new Point(0.1, 0), new Point(0.8, 1),
                ("#262B32", 0), ("#181C22", 0.32), ("#14181E", 0.8), ("#20252C", 1)),
            IsHitTestVisible = false
        };
        AddAt(face, BodyInset + 6, BodyInset + 6);

        var highlight = new Rectangle
        {
            Width = bodySize - 36,
            Height = 1.2,
            Fill = Gradient(new Point(0, 0), new Point(1, 0),
                ("#005D6673", 0), ("#887C8796", 0.38), ("#005D6673", 1)),
            IsHitTestVisible = false
        };
        AddAt(highlight, BodyInset + 18, BodyInset + 5);

        var maker = CreateMaterialText("AGM", 17, "#747E8B");
        maker.FontWeight = FontWeights.SemiBold;
        maker.Width = bodySize - 64;
        maker.TextAlignment = TextAlignment.Center;
        AddAt(maker, BodyInset + 32, BodyInset + bodySize / 2 - 78);

        titleText.FontWeight = FontWeights.SemiBold;
        titleText.Width = bodySize - 54;
        titleText.TextAlignment = TextAlignment.Center;
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        AddAt(titleText, BodyInset + 27, BodyInset + bodySize / 2 - 44);

        packageText.Width = bodySize - 54;
        packageText.TextAlignment = TextAlignment.Center;
        packageText.TextTrimming = TextTrimming.CharacterEllipsis;
        AddAt(packageText, BodyInset + 27, BodyInset + bodySize / 2 - 9);

        selectionText.Width = bodySize - 66;
        selectionText.MaxHeight = 60;
        selectionText.TextAlignment = TextAlignment.Center;
        selectionText.TextWrapping = TextWrapping.Wrap;
        selectionText.TextTrimming = TextTrimming.CharacterEllipsis;
        AddAt(selectionText, BodyInset + 33, BodyInset + bodySize / 2 + 34);

        var pinOneMarker = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Gradient(new Point(0, 0), new Point(1, 1), ("#0A0D12", 0), ("#343D49", 1)),
            Stroke = Brush("#566170"),
            StrokeThickness = 0.7,
            IsHitTestVisible = false
        };
        AddAt(pinOneMarker, BodyInset + 19, BodyInset + 19);
        var pinOneText = CreateMaterialText("PIN 1", 11, "#8D96A3");
        AddAt(pinOneText, BodyInset + 38, BodyInset + 17);

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddLegend(legend, "可分配", "Text");
        AddLegend(legend, "已分配", "RunColor");
        AddLegend(legend, "冲突", "ErrorColor");
        AddLegend(legend, "已选择", "Accent");
        AddLegend(legend, "保留引脚", "Muted", 0.48);
        legend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        AddAt(legend, (Width - legend.DesiredSize.Width) / 2, Height - 22);
    }

    /// <summary>
    /// 用工程提供的全部引脚刷新封装；数量须为 4 的倍数，编号须完整覆盖 1–数量。
    /// 不自动补充功能或可分配性，非法输入在修改呈现前拒绝。
    /// </summary>
    public void SetPins(IReadOnlyList<Ag32PackagePinVisual> pins, int? selectedPin = null)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.Count == 0 || pins.Count % 4 != 0)
        {
            throw new ArgumentException("四边封装的引脚数量必须为非零的 4 的倍数。", nameof(pins));
        }
        var uniquePins = new HashSet<int>();
        foreach (var pin in pins)
        {
            if (pin.Number < 1 || pin.Number > pins.Count || !uniquePins.Add(pin.Number))
            {
                throw new ArgumentException("封装引脚编号必须完整覆盖 1–引脚数量，且不得重复。", nameof(pins));
            }
        }

        pinsPerSide = pins.Count / 4;
        pinVisuals.Clear();
        foreach (var pin in pins)
        {
            pinVisuals.Add(pin.Number, pin);
        }
        this.selectedPin = selectedPin;
        RebuildPresentation();
    }

    private void RebuildPresentation()
    {
        // 引脚宽度不占满间距，外置数字也保持 13 单位字号；高脚数由外层滚动而不是挤压封装。
        bodySize = Math.Max(isQfn ? 260 : 360, (pinsPerSide - 1) * PinPitch + 52);
        pinButtons.Clear();
        pinParts.Clear();
        RenderPackageBody();
        foreach (var pin in pinVisuals.Values.OrderBy(pin => pin.Number))
        {
            var button = CreatePinButton(pin);
            pinButtons.Add(pin.Number, button);
            var (left, top, width, height) = GetPinBounds(pin.Number);
            button.Width = width;
            button.Height = height;
            AddAt(button, left, top);
        }
        SetSelection(selectedPin);
    }

    /// <summary>
    /// 只更新选择状态；传入未呈现的编号或 null 时清除选择。
    /// </summary>
    public void SetSelection(int? pinNumber)
    {
        selectedPin = pinNumber is int candidatePin && pinVisuals.ContainsKey(candidatePin) ? candidatePin : null;
        foreach (var number in pinButtons.Keys)
        {
            UpdatePinState(number);
        }

        if (selectedPin is int selected && pinVisuals.TryGetValue(selected, out var visual))
        {
            var function = string.IsNullOrWhiteSpace(visual.Function) ? "未分配" : visual.Function;
            selectionText.Text = $"PIN {selected}\n{(visual.CanAssign ? function : "保留引脚 · 不可分配")}";
            selectionText.ToolTip = visual.Conflict ?? visual.Function;
        }
        else
        {
            selectionText.Text = "点击引脚查看与分配";
            selectionText.ToolTip = null;
        }
    }

    /// <summary>
    /// 点击任意引脚时报告封装编号；保留引脚也可被选择以查看详情。
    /// </summary>
    public event EventHandler<int>? PinSelected;

    private Button CreatePinButton(Ag32PackagePinVisual pin)
    {
        var side = (pin.Number - 1) / pinsPerSide;
        var vertical = side is 1 or 3;
        var (_, _, width, height) = GetPinBounds(pin.Number);
        var content = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        var label = CreateText(pin.Number.ToString(CultureInfo.InvariantCulture), 13);
        label.FontWeight = FontWeights.SemiBold;
        label.TextAlignment = TextAlignment.Center;
        label.Width = vertical ? width : 28;
        label.Height = 24;
        var lead = CreatePinLead(vertical);
        // 功能名直接对齐在整条着色的引脚上；上下两侧旋转文字，避免相邻引脚标签重叠。
        var functionLabel = new TextBlock
        {
            Text = pin.Function ?? "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#101820"),
            Width = PinLength - 6,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            LayoutTransform = vertical ? new RotateTransform(-90) : Transform.Identity
        };
        lead.Child = functionLabel;
        var marker = new Border
        {
            Width = vertical ? 10 : 4,
            Height = vertical ? 4 : 10,
            CornerRadius = new CornerRadius(1)
        };
        var cross = vertical ? width / 2 : height / 2;
        switch (side)
        {
            case 0:
                PlaceNumber(content, label, 0, cross - 12);
                Place(content, marker, 32, cross - 5);
                Place(content, lead, LabelSpace, cross - lead.Height / 2);
                break;
            case 1:
                Place(content, lead, cross - lead.Width / 2, 0);
                Place(content, marker, cross - 5, PinLength + 6);
                PlaceNumber(content, label, 0, PinLength + 16);
                break;
            case 2:
                Place(content, lead, 0, cross - lead.Height / 2);
                Place(content, marker, PinLength + 6, cross - 5);
                PlaceNumber(content, label, PinLength + 14, cross - 12);
                break;
            default:
                PlaceNumber(content, label, 0, 0);
                Place(content, marker, cross - 5, 32);
                Place(content, lead, cross - lead.Width / 2, LabelSpace);
                break;
        }
        pinParts.Add(pin.Number, new(lead, marker, label));

        // 引脚填充表达分配与冲突，选择仅使用外框，不盖住冲突颜色。
        var button = new Button
        {
            Style = null,
            Template = PinTemplate,
            Content = content,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
            Focusable = true,
            IsTabStop = true,
            TabIndex = pin.Number,
            Tag = pin.Number
        };
        var function = string.IsNullOrWhiteSpace(pin.Function) ? "未分配" : pin.Function;
        var details = $"PIN {pin.Number}\n{(pin.CanAssign ? "可分配" : "保留引脚 · 不可分配")}\n{function}";
        if (pin.Conflict is not null)
        {
            details += "\n" + pin.Conflict;
        }
        var toolTip = new ToolTip { Content = details };
        toolTip.SetResourceReference(Control.BackgroundProperty, "Surface");
        toolTip.SetResourceReference(Control.ForegroundProperty, "Text");
        toolTip.SetResourceReference(Control.BorderBrushProperty, "Border");
        button.ToolTip = toolTip;
        AutomationProperties.SetName(button, $"引脚 {pin.Number}");
        AutomationProperties.SetHelpText(button, details);
        button.MouseEnter += (_, _) => UpdatePinState(pin.Number);
        button.MouseLeave += (_, _) => UpdatePinState(pin.Number);
        button.GotKeyboardFocus += (_, _) => UpdatePinState(pin.Number);
        button.LostKeyboardFocus += (_, _) => UpdatePinState(pin.Number);
        button.Click += (_, _) =>
        {
            SetSelection(pin.Number);
            PinSelected?.Invoke(this, pin.Number);
        };
        return button;
    }

    private void UpdatePinState(int number)
    {
        if (!pinButtons.TryGetValue(number, out var button) || !pinParts.TryGetValue(number, out var parts))
        {
            return;
        }
        var pin = pinVisuals[number];
        var isSelected = selectedPin == number;
        var isAssigned = pin.CanAssign && !string.IsNullOrWhiteSpace(pin.Function);
        var interaction = isSelected || button.IsMouseOver || button.IsKeyboardFocused;
        if (interaction)
        {
            button.SetResourceReference(Control.BorderBrushProperty, "Accent");
        }
        else
        {
            button.BorderBrush = Brushes.Transparent;
        }
        var resource = pin.Conflict is not null ? "ErrorColor" : isAssigned ? "RunColor" : isSelected ? "Accent" : pin.CanAssign ? "Text" : "Muted";
        parts.Label.SetResourceReference(TextBlock.ForegroundProperty, resource);
        parts.Marker.SetResourceReference(Border.BackgroundProperty, resource);
        parts.Marker.Opacity = isSelected || isAssigned ? 1 : pin.CanAssign ? 0.4 : 0.22;
        parts.Lead.Opacity = pin.CanAssign || isSelected ? 1 : 0.58;
        if (pin.Conflict is not null || isAssigned)
        {
            parts.Lead.SetResourceReference(Border.BackgroundProperty, pin.Conflict is not null ? "ErrorColor" : "RunColor");
        }
        else
        {
            parts.Lead.Background = Brush("#8C9198");
        }
    }

    private Border CreatePinLead(bool vertical)
    {
        const double thickness = 18;
        var width = vertical ? thickness : PinLength;
        var height = vertical ? PinLength : thickness;
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            Background = Brush("#8C9198"),
            IsHitTestVisible = false
        };
    }

    private (double Left, double Top, double Width, double Height) GetPinBounds(int number)
    {
        // 从左上 PIN 1 开始逆时针编号，右侧与上侧的画面顺序须反转。
        var side = (number - 1) / pinsPerSide;
        var index = (number - 1) % pinsPerSide;
        var margin = (bodySize - (pinsPerSide - 1) * PinPitch) / 2;
        var forward = BodyInset + margin + index * PinPitch;
        var reverse = BodyInset + margin + (pinsPerSide - index - 1) * PinPitch;
        var length = PinLength + LabelSpace;
        return side switch
        {
            0 => (BodyInset - length, forward - 12, length, 24),
            1 => (forward - 12, BodyInset + bodySize, 24, length),
            2 => (BodyInset + bodySize, reverse - 12, length, 24),
            _ => (reverse - 12, BodyInset - length, 24, length)
        };
    }

    private static ControlTemplate CreatePinTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
        root.AppendChild(content);
        var outline = new FrameworkElementFactory(typeof(Border));
        outline.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        outline.SetValue(Border.BorderThicknessProperty, new Thickness(1));
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

    private static LinearGradientBrush Gradient(Point start, Point end, params (string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
        }
        brush.Freeze();
        return brush;
    }

    private static TextBlock CreateMaterialText(string text, double fontSize, string color)
    {
        return new TextBlock { Text = text, FontSize = fontSize, Foreground = Brush(color), IsHitTestVisible = false };
    }

    private static TextBlock CreateText(string text, double fontSize, string color = "Text")
    {
        var block = new TextBlock { Text = text, FontSize = fontSize };
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    private static void AddLegend(StackPanel legend, string text, string color, double opacity = 1)
    {
        var marker = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Opacity = opacity,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        marker.SetResourceReference(Border.BackgroundProperty, color);
        legend.Children.Add(marker);
        var label = CreateText(text, 12, "Muted");
        label.Margin = new Thickness(0, 0, 14, 0);
        legend.Children.Add(label);
    }

    private static void Place(Canvas parent, UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
        parent.Children.Add(element);
    }

    private static void PlaceNumber(Canvas parent, TextBlock label, double left, double top)
    {
        // 透明壁纸可能正好处于亮区；只给数字独立底片，文字保持全不透明。
        var plate = new Border
        {
            Width = label.Width,
            Height = label.Height,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(0.5),
            Opacity = 0.82,
            IsHitTestVisible = false
        };
        plate.SetResourceReference(Border.BackgroundProperty, "Panel");
        plate.SetResourceReference(Border.BorderBrushProperty, "Border");
        Place(parent, plate, left, top);
        label.Padding = new Thickness(0, 3, 0, 0);
        Place(parent, label, left, top);
    }

    private void AddAt(UIElement element, double left, double top) => Place(this, element, left, top);

    private sealed record PinParts(Border Lead, Border Marker, TextBlock Label);
}

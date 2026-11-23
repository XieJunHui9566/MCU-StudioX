namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StudioX.Engine;

/// <summary>四边封装的顶视引脚分配图，编号与可分配性由器件数据提供。</summary>
public sealed class Ag32PackageDiagram : Canvas
{
    private const double Pitch = 18;
    private const double Pad = 29;
    private const double Gap = 4;
    private const double MaxCaptionWidth = 110;
    private static readonly SolidColorBrush BodyFill = FrozenBrush(78, 83, 86);
    private static readonly SolidColorBrush Ink = FrozenBrush(24, 30, 32);
    // 封装格和图例共享固定的引脚类别色，诊断和选择仍跟随工作台主题。
    public static SolidColorBrush AvailablePadBrush { get; } = FrozenBrush(204, 211, 213);
    public static SolidColorBrush SupplyPadBrush { get; } = FrozenBrush(246, 162, 122);
    public static SolidColorBrush GroundPadBrush { get; } = FrozenBrush(125, 184, 235);
    public static SolidColorBrush ReservedPadBrush { get; } = FrozenBrush(232, 202, 141);
    private static readonly ControlTemplate PadTemplate = CreatePadTemplate();
    private readonly Border body = new() { Background = BodyFill, BorderBrush = Ink, BorderThickness = new Thickness(1) };
    private readonly Ellipse origin = new() { Width = 6, Height = 6, Fill = Brushes.WhiteSmoke, IsHitTestVisible = false };
    private readonly TextBlock device = BodyLabel(17);
    private readonly TextBlock package = BodyLabel(14);
    private readonly TextBlock selection = BodyLabel(12);
    private sealed record Tile(Button Button, TextBlock Caption);
    private readonly Dictionary<int, Tile> tiles = [];
    private readonly Dictionary<int, Ag32PackagePinVisual> pins = [];
    private readonly double[] margins = new double[4];
    private int? selectedPin, txPin, rxPin, rtsPin, ctsPin;
    private string? selectedUart;

    public Ag32PackageDiagram()
    {
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
        labels.Children.Add(device);
        labels.Children.Add(package);
        selection.Margin = new Thickness(0, 9, 0, 0);
        labels.Children.Add(selection);
        body.Child = labels;
        Children.Add(body);
        Children.Add(origin);
        Background = Brushes.Transparent;
        ClipToBounds = true;
    }

    public event EventHandler<int>? PinSelected;

    public void SetPackage(string deviceId, string packageName)
    {
        device.Text = deviceId;
        package.Text = packageName;
        AutomationProperties.SetName(this, $"{deviceId} {packageName} 封装顶视图");
    }

    public void SetPins(IReadOnlyList<Ag32PackagePinVisual> values, int? selectedPin = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Count % 4 != 0 ||
            values.Any(pin => pin.Number < 1 || pin.Number > values.Count) || values.DistinctBy(pin => pin.Number).Count() != values.Count)

        {
            throw new ArgumentException("四边封装的引脚必须完整覆盖 1 到引脚总数，且不得重复。", nameof(values));
        }

        var rebuild = values.Count != tiles.Count;
        if (rebuild)
        {
            foreach (var tile in tiles.Values)
            {
                Children.Remove(tile.Button);
                Children.Remove(tile.Caption);
            }
            tiles.Clear();
            pins.Clear();
        }
        var layoutChanged = rebuild;
        foreach (var pin in values)
        {
            if (pins.TryGetValue(pin.Number, out var previous) && previous == pin)
            {
                continue;
            }
            pins[pin.Number] = pin;
            if (rebuild)
            {
                tiles.Add(pin.Number, CreateTile(pin.Number, (pin.Number - 1) / (values.Count / 4)));
            }
            var tile = tiles[pin.Number];
            var caption = pin.FixedPin?.Name ?? (string.IsNullOrEmpty(pin.Function) ? pin.AlternateFunction ?? "" : pin.Function).Replace("_UARTTXD", "_TX", StringComparison.Ordinal)
                .Replace("_UARTRXD", "_RX", StringComparison.Ordinal)
                .Replace("_NUARTRTS", "_nRTS", StringComparison.Ordinal)
                .Replace("_NUARTCTS", "_nCTS", StringComparison.Ordinal);
            layoutChanged |= tile.Caption.Text != caption;
            tile.Caption.Text = caption;
            var details = pin.FixedPin is { } fixedPin
                ? $"PIN_{pin.Number} · {fixedPin.Name}\n{fixedPin.Description} · 固定连接"
                : $"PIN_{pin.Number} · {(pin.CanAssign ? "可分配" : "保留引脚")}\n{(string.IsNullOrEmpty(pin.Function) ? "未分配功能" : pin.Function)}";
            if (pin.AlternateFunction is not null)
            {
                details += $"\n可复用为 {pin.AlternateFunction}";
            }
            if (pin.Conflict is not null)
            {
                details += "\n" + pin.Conflict;
            }
            tile.Button.ToolTip = tile.Caption.ToolTip = details;
            AutomationProperties.SetName(tile.Button, $"引脚 {pin.Number} {caption}".TrimEnd());
            AutomationProperties.SetHelpText(tile.Button, details);
            Paint(pin.Number);
        }
        if (layoutChanged)
        {
            ArrangePackage();
        }
        SetSelection(selectedPin);
    }

    public void SetSelection(int? number)
    {
        var previous = selectedPin;
        selectedPin = number is int value && pins.ContainsKey(value) ? value : null;
        Paint(previous);
        Paint(selectedPin);
        UpdateSummary();
    }

    internal void ShowUartPins(string? uart, int? tx = null, int? rx = null, int? rts = null, int? cts = null)
    {
        if ((selectedUart, txPin, rxPin, rtsPin, ctsPin) == (uart, tx, rx, rts, cts))
        {
            return;
        }
        var changed = new[] { txPin, rxPin, rtsPin, ctsPin, tx, rx, rts, cts, selectedPin };
        (selectedUart, txPin, rxPin, rtsPin, ctsPin) = (uart, tx, rx, rts, cts);
        foreach (var number in changed)
        {
            Paint(number);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        selection.Text = selectedUart is not null ? $"{selectedUart}   TX {txPin?.ToString() ?? "—"}   RX {rxPin?.ToString() ?? "—"}"
            : selectedPin is int pin ? $"PIN_{pin}" : "";
        if (selectedUart is not null && (rtsPin.HasValue || ctsPin.HasValue))

        {
            selection.Text += $"\nRTS {rtsPin?.ToString() ?? "—"}   CTS {ctsPin?.ToString() ?? "—"}";
        }
        selection.Visibility = selection.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Tile CreateTile(int number, int side)
    {
        var vertical = side is 1 or 3;
        var numberText = new TextBlock
        {
            Text = number.ToString(CultureInfo.InvariantCulture),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = vertical ? new RotateTransform(-90) : Transform.Identity
        };
        var button = new Button
        {
            Tag = number,
            Content = numberText,
            Template = PadTemplate,
            Style = null,
            Width = vertical ? Pitch : Pad,
            Height = vertical ? Pad : Pitch,
            Cursor = Cursors.Hand,
            TabIndex = number,
            FocusVisualStyle = null
        };
        AutomationProperties.SetName(button, $"引脚 {number}");
        button.MouseEnter += (_, _) => Paint(number);
        button.MouseLeave += (_, _) => Paint(number);
        button.IsKeyboardFocusedChanged += (_, _) => Paint(number);
        button.Click += (_, _) =>
        {
            SetSelection(number);
            PinSelected?.Invoke(this, number);
        };
        var caption = new TextBlock
        {
            Tag = number,
            MaxWidth = MaxCaptionWidth,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LayoutTransform = vertical ? new RotateTransform(-90) : Transform.Identity,
            Cursor = Cursors.Hand
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        caption.MouseLeftButtonUp += (_, e) =>
        {
            button.Focus();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        Children.Add(button);
        Children.Add(caption);
        return new(button, caption);
    }

    private void ArrangePackage()
    {
        var perSide = tiles.Count / 4;
        var sideLength = perSide * Pitch + 12;
        Array.Fill(margins, Pad + Gap + 2);
        foreach (var (number, tile) in tiles)
        {
            if (tile.Caption.Text.Length == 0)
            {
                continue;
            }
            var side = (number - 1) / perSide;
            tile.Caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var length = side is 1 or 3 ? tile.Caption.DesiredSize.Height : tile.Caption.DesiredSize.Width;
            margins[side] = Math.Max(margins[side], Pad + Gap + length + 2);
        }
        var x = margins[0];
        var y = margins[3];
        Width = x + sideLength + margins[2];
        Height = y + sideLength + margins[1];
        body.Width = body.Height = sideLength;
        Place(body, x, y);
        Place(origin, x + 6, y + 6);
        foreach (var (number, tile) in tiles)
        {
            // 从左上方一号脚开始，依次沿左、下、右、上四条边逆时针编号。
            var side = (number - 1) / perSide;
            var index = (number - 1) % perSide;
            var offset = 6 + (side >= 2 ? perSide - 1 - index : index) * Pitch;
            var left = side switch
            {
                0 => x - Pad,
                2 => x + sideLength,
                _ => x + offset
            };
            var top = side switch
            {
                1 => y + sideLength,
                3 => y - Pad,
                _ => y + offset
            };
            Place(tile.Button, left, top);
            if (tile.Caption.Text.Length == 0)
            {
                continue;
            }
            var size = tile.Caption.DesiredSize;
            var cx = side switch
            {
                0 => left - Gap - size.Width,
                2 => left + Pad + Gap,
                _ => left + (Pitch - size.Width) / 2
            };
            var cy = side switch
            {
                1 => top + Pad + Gap,
                3 => top - Gap - size.Height,
                _ => top + (Pitch - size.Height) / 2
            };
            Place(tile.Caption, cx, cy);
        }
    }

    private void Paint(int? number)
    {
        if (number is not int n || !tiles.TryGetValue(n, out var tile))
        {
            return;
        }
        var pin = pins[n];
        var active = selectedUart is null ? n == selectedPin : n == txPin || n == rxPin || n == rtsPin || n == ctsPin;
        if (pin.Conflict is not null)
        {
            tile.Button.SetResourceReference(Control.BackgroundProperty, "ErrorColor");
        }
        else if (pin.FixedPin is { } fixedPin)
        {
            tile.Button.Background = fixedPin.Kind switch
            {
                Ag32FixedPinKind.Supply => SupplyPadBrush,
                Ag32FixedPinKind.Ground => GroundPadBrush,
                _ => ReservedPadBrush
            };
        }
        else if (!string.IsNullOrWhiteSpace(pin.Function))
        {
            tile.Button.SetResourceReference(Control.BackgroundProperty, "RunColor");
        }
        else
        {
            tile.Button.Background = pin.CanAssign ? AvailablePadBrush : ReservedPadBrush;
        }
        // 使用粗轮廓表达选择与焦点，功能填色仍然可辨。
        var emphasis = active || tile.Button.IsKeyboardFocused || tile.Button.IsMouseOver;
        tile.Button.BorderBrush = Ink;
        tile.Button.BorderThickness = new Thickness(emphasis ? 2 : 0.5);
        tile.Caption.SetResourceReference(TextBlock.ForegroundProperty, pin.Conflict is not null ? "ErrorColor" : active ? "Accent" : "Text");
    }

    private static ControlTemplate CreatePadTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })

        {
            border.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        }
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static TextBlock BodyLabel(double size) => new()
    {
        FontSize = size,
        Foreground = Brushes.WhiteSmoke,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeights.SemiBold
    };

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void Place(UIElement element, double left, double top)
    {
        SetLeft(element, left);
        SetTop(element, top);
    }
}

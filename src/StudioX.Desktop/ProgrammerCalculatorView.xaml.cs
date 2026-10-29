namespace StudioX.Desktop;

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application.Plugins;
using StudioX.Extensions.Abstractions;

/// <summary>宿主拥有的计算器界面；只编辑输入并向隔离插件发送命令，不执行插件程序集或整数运算。</summary>
internal partial class ProgrammerCalculatorView : UserControl
{
    private readonly Func<JsonElement, Task> submit;
    private readonly Action<string> diagnostic;
    private PluginProgrammerCalculatorState state = null!;
    private readonly Dictionary<int, Button> bases = [];
    private readonly Dictionary<int, TextBlock> readings = [];
    private readonly Dictionary<int, Button> widths = [];
    private readonly List<(string Key, Button Button)> digitKeys = [];
    private bool applyingState;
    private bool draftChanged;
    private long draftRevision;
    private readonly List<(Button Button, TextBlock Value)> bitViews = [];

    internal ProgrammerCalculatorView(JsonElement value, Func<JsonElement, Task> submit, Action<string> diagnostic)
    {
        this.submit = submit;
        this.diagnostic = diagnostic;
        InitializeComponent();
        RefreshPalette();
        foreach (var (radix, title) in new[] { (16, "HEX"), (10, "DEC"), (8, "OCT"), (2, "BIN") })
        {
            var reading = new TextBlock { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var row = new Grid();
            row.ColumnDefinitions.Add(new()
            {
                Width = new GridLength(48)
            });
            row.ColumnDefinitions.Add(new());
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            Grid.SetColumn(reading, 1);
            row.Children.Add(reading);
            var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 8, 6), MinHeight = 31 };
            AutomationProperties.SetName(button, title + " 输入进制");
            button.Click += async (_, _) => await ConfigureAsync("radix", radix.ToString(CultureInfo.InvariantCulture));
            BaseRows.Children.Add(button);
            bases.Add(radix, button);
            readings.Add(radix, reading);
        }
        foreach (var (width, title) in new[] { (8, "BYTE"), (16, "WORD"), (32, "DWORD"), (64, "QWORD") })
        {
            var button = new Button { Content = title, FontSize = 11, Padding = new Thickness(7, 6, 7, 6), ToolTip = $"{width} 位；缩窄时保留低位" };
            button.Click += async (_, _) => await ConfigureAsync("width", width.ToString(CultureInfo.InvariantCulture));
            WidthButtons.Children.Add(button);
            widths.Add(width, button);
        }
        string[] keys = ["A", "(", ")", "CE", "清空", "⌫", "B", "7", "8", "9", "%", "÷",
            "C", "4", "5", "6", "AND", "×", "D", "1", "2", "3", "OR", "−",
            "E", "±", "0", "XOR", "NOT", "+", "F", "<<", ">>", "ROL", "ROR", "="];
        foreach (var key in keys)
        {
            var button = new Button { Content = key, MinHeight = 49, FontSize = key.Length > 2 ? 12 : 19, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (key.Length == 1 && "0123456789ABCDEF".Contains(key, StringComparison.Ordinal))
            {
                button.SetResourceReference(BackgroundProperty, "CalcKey");
                digitKeys.Add((key, button));
            }
            if (key == "=")
            {
                button.SetResourceReference(BackgroundProperty, "CalcAccent");
                button.SetResourceReference(ForegroundProperty, "CalcOnAccent");
            }
            AutomationProperties.SetName(button, key == "⌫" ? "退格" : key);
            button.Click += async (_, _) => await PressAsync(key);
            Keypad.Children.Add(button);
        }
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 680;
            DetailsColumn.Width = new GridLength(compact ? 0 : 280);
            Grid.SetColumn(McuDetails, compact ? 0 : 1);
            Grid.SetRow(McuDetails, compact ? 1 : 0);
            McuDetails.Margin = new Thickness(compact ? 0 : 8, compact ? 16 : 0, 0, 0);
            CalculatorBody.Margin = new Thickness(0, 0, compact ? 0 : 14, 0);
        };
        Expression.TextChanged += (_, _) =>
        {
            if (!applyingState)
            {
                draftChanged = true;
                draftRevision++;
                SchedulePreview();
            }
        };
        Update(value);
        Loaded += (_, _) => { Expression.Focus(); Expression.SelectAll(); };
        Unloaded += (_, _) => previewTimer.Stop();
    }

    internal void Update(JsonElement value)
    {
        var next = PluginProgrammerCalculator.Parse(value);
        if (state is null)
        {
            latestRequestId = next.RequestId;
        }
        if (next.RequestId > 0 && (next.RequestId < latestRequestId || next.RequestId <= appliedRequestId))
        {
            return;
        }
        if (next.RequestId > 0)
        {
            appliedRequestId = next.RequestId;
        }
        var acceptInput = !draftChanged || (submittedRevisions.TryGetValue(next.RequestId, out var submitted) && submitted == draftRevision);
        if (!next.Realtime && pumping && submittedRevisions.TryGetValue(latestRequestId, out var legacyRevision) && legacyRevision == draftRevision)
        {
            acceptInput = true;
        }
        state = next;
        PreviousExpression.Text = state.Expression + (state.FieldMode ? "  · 位域" : " =");
        if (state.Error is null && !state.Preview && acceptInput)
        {
            applyingState = true;
            Expression.Text = SelectedValue;
            Expression.SelectAll();
            applyingState = false;
            draftChanged = false;
        }
        StatusText.Text = state.Error is { } error ? error + "（保留上次成功结果）" : state.Pending ? "输入中…" : state.Realtime ? "进制随输入更新 · 移位键立即执行 1 位 · Esc 清空" : "Enter 计算 · Esc 清空";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, state.Error is null ? "CalcMuted" : "DiagnosticError");
        readings[16].Text = state.Hex;
        readings[10].Text = state.DecimalText;
        readings[8].Text = state.Octal;
        readings[2].Text = string.Join(" ", Enumerable.Range(0, state.Width / 4).Select(i => state.Binary.Substring(i * 4, 4)));
        foreach (var (radix, button) in bases)
        {
            button.SetResourceReference(BackgroundProperty, radix == state.Radix ? "CalcHover" : "CalcBackground");
            button.SetResourceReference(ForegroundProperty, radix == state.Radix ? "CalcAccent" : "CalcText");
        }
        foreach (var (width, button) in widths)
        {
            button.SetResourceReference(BackgroundProperty, width == state.Width ? "CalcHover" : "CalcSurface");
        }
        foreach (var (key, button) in digitKeys)
        {
            var digit = "0123456789ABCDEF".IndexOf(key, StringComparison.Ordinal);
            button.IsEnabled = digit < state.Radix;
        }
        SignedButton.Content = state.SignedMode ? "有符号" : "无符号";
        IntegerInterpretation.Text = $"{state.Width} 位\n无符号 {state.Unsigned}\n有符号 {state.Signed}";
        LittleEndian.Text = state.LittleEndian;
        BigEndian.Text = state.BigEndian;
        Constant.Text = $"UINT{state.Width}_C(0x{state.Hex})";
        FieldControls.Visibility = state.FieldMode ? Visibility.Visible : Visibility.Collapsed;
        if (state.Error is null && !state.Preview && acceptInput)
        {
            FieldStart.Text = state.Start.ToString(CultureInfo.InvariantCulture);
            FieldCount.Text = state.Count.ToString(CultureInfo.InvariantCulture);
            FieldOperand.Text = state.Operand;
        }
        CalculatorMode.SetResourceReference(BackgroundProperty, state.FieldMode ? "CalcSurface" : "CalcHover");
        FieldMode.SetResourceReference(BackgroundProperty, state.FieldMode ? "CalcHover" : "CalcSurface");
        if (bitViews.Count != state.Width)
        {
            Bits.Children.Clear();
            bitViews.Clear();
            for (var bit = state.Width - 1; bit >= 0; bit--)
            {
                var index = bit;
                var body = new StackPanel();
                var caption = new TextBlock { Text = bit.ToString(CultureInfo.InvariantCulture), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "CalcMuted");
                body.Children.Add(caption);
                var bitValue = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
                body.Children.Add(bitValue);
                var button = new Button { Content = body, Padding = new Thickness(1, 3, 1, 3), ToolTip = $"翻转 bit {bit}" };
                AutomationProperties.SetName(button, $"bit {bit}");
                button.Click += async (_, _) => await SendAsync("bit", bit: index);
                Bits.Children.Add(button);
                bitViews.Add((button, bitValue));
            }
        }
        for (var index = 0; index < bitViews.Count; index++)
        {
            var bit = state.Width - 1 - index;
            bitViews[index].Value.Text = state.Binary[index].ToString();
            bitViews[index].Button.BorderThickness = state.FieldMode && bit >= state.Start && bit < state.Start + state.Count ? new Thickness(0, 0, 0, 2) : new Thickness(0);
            bitViews[index].Button.SetResourceReference(BorderBrushProperty, "CalcAccent");
        }
        FieldResult.Text = $"bit {state.Start}–{state.Start + state.Count - 1}\n掩码 0x{state.FieldMask}\n提取值 {state.FieldValue}";
    }

    private string SelectedValue => state.Radix switch { 16 => state.Hex, 10 => state.DecimalText, 8 => state.Octal, _ => state.Binary };
    private Task ConfigureAsync(string setting, string choice) => SendAsync("configure", setting, choice);
    private async Task PressAsync(string key)
    {
        switch (key)
        {
            case "=":
                await SendAsync("calculate");
                return;
            case "清空":
                await ClearAsync();
                return;
            case "CE":
                await ClearAsync();
                return;
            case "⌫":
                if (Expression.SelectionLength > 0)
                {
                    Expression.SelectedText = "";
                }
                else if (Expression.CaretIndex > 0)
                {
                    var position = Expression.CaretIndex;
                    Expression.Text = Expression.Text.Remove(position - 1, 1);
                    Expression.CaretIndex = position - 1;
                }
                return;
            case "NOT":
                if (state.Realtime)
                {
                    await SendAsync("unary", operation: "not");
                }
                else
                {
                    Expression.Text = "~(" + Expression.Text + ")";
                    await SendAsync("calculate");
                }
                return;
            case "±":
                if (state.Realtime)
                {
                    await SendAsync("unary", operation: "-");
                }
                else
                {
                    Expression.Text = "-(" + Expression.Text + ")";
                    await SendAsync("calculate");
                }
                return;
            case "<<":
            case ">>":
            case ">>>":
            case "ROL":
            case "ROR":
                if (!state.Realtime)
                {
                    break;
                }
                await SendAsync("shift", operation: key switch
                {
                    "<<" => "shl",
                    ">>" => "shr",
                    "ROL" => "rol",
                    "ROR" => "ror",
                    _ => ">>>"
                });
                return;
        }
        var token = key switch
        {
            "×" => "*",
            "÷" => "/",
            "−" => "-",
            _ => key
        };
        var digit = token.Length == 1 && "0123456789ABCDEF".Contains(token, StringComparison.Ordinal);
        if (!digit && token is not ("(" or ")"))
        {
            Expression.CaretIndex = Expression.Text.Length;
            Expression.SelectionLength = 0;
            token = " " + token + " ";
        }
        Expression.SelectedText = token;
        Expression.CaretIndex = Expression.SelectionStart + Expression.SelectionLength;
        Expression.SelectionLength = 0;
        Expression.Focus();
    }

    private async void Calculator_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendAsync("calculate");
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await ClearAsync();
        }
    }
    private async void Signed_Click(object sender, RoutedEventArgs e) => await ConfigureAsync("signedMode", state.SignedMode ? "false" : "true");
    private async void Mode_Click(object sender, RoutedEventArgs e) => await SendAsync((string)((Button)sender).Tag);
    private async void Field_Click(object sender, RoutedEventArgs e) => await SendAsync("calculate", operation: (string)((Button)sender).Tag);
    private async void ExtraOperator_Click(object sender, RoutedEventArgs e) => await PressAsync((string)((Button)sender).Tag);
    private void CopyValue_Click(object sender, RoutedEventArgs e) => Copy(SelectedValue);
    private void CopyConstant_Click(object sender, RoutedEventArgs e) => Copy(Constant.Text);
    private void CopyAll_Click(object sender, RoutedEventArgs e) => Copy(state.CopyText);
    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = "已复制";
        }
        catch (Exception error) { diagnostic(error.ToString()); StatusText.Text = "复制失败：" + error.Message; }
    }
    internal void RefreshPalette()
    {
        var light = System.Windows.Application.Current.TryFindResource("Background") is SolidColorBrush background &&
            background.Color.R * 0.299 + background.Color.G * 0.587 + background.Color.B * 0.114 > 150;
        var palette = light ? new[] { ("CalcBackground", "#F3F3F3"), ("CalcSurface", "#EAEAEA"), ("CalcKey", "#FFFFFF"),
            ("CalcHover", "#DCE8F5"), ("CalcText", "#202020"), ("CalcMuted", "#606060"), ("CalcBorder", "#C8C8C8"),
            ("CalcAccent", "#0067C0"), ("CalcOnAccent", "#FFFFFF") } : new[] { ("CalcBackground", "#202020"), ("CalcSurface", "#292929"), ("CalcKey", "#343434"),
            ("CalcHover", "#454545"), ("CalcText", "#F5F5F5"), ("CalcMuted", "#B6B6B6"), ("CalcBorder", "#454545"),
            ("CalcAccent", "#99C9FF"), ("CalcOnAccent", "#102840") };
        foreach (var (key, color) in palette)
        {
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}

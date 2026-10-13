namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.StcTiming;

internal sealed class StcTimingWindow : Window
{
    internal TextBox ClockField { get; } = new() { MaxLength = 10 };
    internal TextBox DelayField { get; } = new() { MaxLength = 9 };
    internal TextBox BaudField { get; } = new() { MaxLength = 8 };
    internal TextBox NameField { get; } = new() { Text = "sx_delay", MaxLength = 39 };
    internal ComboBox KindPicker { get; } = new();
    internal ComboBox DividerPicker { get; } = new();
    internal ComboBox TimerPicker { get; } = new();
    internal ComboBox GeneratorPicker { get; } = new();
    internal ComboBox PinsPicker { get; } = new();
    internal TextBox PreviewField { get; } = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal Button InsertButton { get; } = new() { Content = "插入当前 C 文件顶部", MinWidth = 170 };
    internal TextBlock SummaryField { get; } = new() { TextWrapping = TextWrapping.Wrap };
    internal StcTimingPreview? PreviewResult
    {
        get; private set;
    }
    public StcTimingContext Context
    {
        get; private set;
    }
    public StcTimingPreview? Result
    {
        get; private set;
    }
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button copy = new() { Content = "复制代码", MinWidth = 90, Margin = new(0, 0, 8, 0) };
    private readonly Button save = new() { Content = "保存生成参数", MinWidth = 120, Margin = new(0, 0, 8, 0) };
    private readonly bool insertable;
    private readonly StcTimingService service;
    private readonly Action<string> log;
    private bool ready;
    private bool busy;

    public StcTimingWindow(StcTimingService service, StcTimingContext context, bool insertable, Action<string> log)
    {
        this.service = service;
        this.insertable = insertable;
        this.log = log;
        Context = context;
        Title = "51 延时与波特率 · " + context.Project.DeviceId;
        Width = 1020;
        Height = 760;
        MinWidth = 760;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var root = new Grid { Margin = new(18) };
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var intro = new TextBlock
        {
            Text = $"{context.Project.DeviceId} · {context.Profile.Family.ToUpperInvariant()} · SDCC\n" +
                (context.ProjectClockHz is { } hz ? $"采用已保存工程时钟：{hz:N0} Hz。" : "工程未指定时钟，请填写实际系统时钟；保存仅记录参考值，不配置芯片。") +
                "计时频率须为运行时分频后的系统时钟。" + (context.Mon51 ? " Mon51：P3.0/P3.1 专供监控。" : ""),
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 12)
        };
        root.Children.Add(intro);
        var body = new Grid();
        body.ColumnDefinitions.Add(new()
        {
            Width = new(270)
        });
        body.ColumnDefinitions.Add(new()
        {
            Width = new(16)
        });
        body.ColumnDefinitions.Add(new());
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var fields = new StackPanel();
        var scroller = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(scroller);
        KindPicker.ItemsSource = new[] { "固定延时函数", "UART1 波特率初始化" };
        KindPicker.SelectedIndex = 0;
        DividerPicker.ItemsSource = context.Profile.TimerDividers;
        DividerPicker.SelectedItem = context.Settings.TimerDivider;
        TimerPicker.ItemsSource = new[] { 0, 1 };
        TimerPicker.SelectedItem = context.Settings.DelayTimer;
        GeneratorPicker.ItemsSource = context.Profile.HasTimer2Baud ? new[] { "timer1", "timer2" } : new[] { "timer1" };
        GeneratorPicker.SelectedItem = context.Settings.BaudGenerator;
        PinsPicker.ItemsSource = context.Profile.HasPinSwitch ? new[] { "default", "p36", "p16" } : new[] { "default" };
        PinsPicker.SelectedItem = context.Settings.UartPins;
        ClockField.Text = context.ClockHz?.ToString(CultureInfo.InvariantCulture) ?? "";
        ClockField.IsReadOnly = context.ProjectClockHz is not null;
        DelayField.Text = context.Settings.DelayMicroseconds.ToString(CultureInfo.InvariantCulture);
        BaudField.Text = context.Settings.BaudRate.ToString(CultureInfo.InvariantCulture);
        Add("生成类型", KindPicker);
        Add("系统时钟（整数 Hz）", ClockField);
        Add(context.Profile.Family == "stc89" ? "计时分频（须与 ISP 的 6T/12T 一致）" : "定时器计时分频（1T / 12T）", DividerPicker);
        Add("函数名（sx_ 前缀）", NameField);
        Add("固定延时（µs；1000 = 1 ms）", DelayField);
        Add("延时使用的定时器", TimerPicker);
        Add("用户 UART1 波特率（与 ISP 速度独立）", BaudField);
        Add("波特率发生器（timer1 / timer2）", GeneratorPicker);
        Add("UART1 RX/TX（default=P3.0/3.1）", PinsPicker);
        fields.Children.Add(new TextBlock { Text = "p36 = P3.6/P3.7；p16 = P1.6/P1.7。\n生成函数独占选定的资源，请检查返回值。", TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(PreviewField, 2);
        body.Children.Add(PreviewField);
        PreviewField.SetResourceReference(BackgroundProperty, "EditorSurface");
        PreviewField.SetResourceReference(ForegroundProperty, "Text");
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        footer.Children.Add(SummaryField);
        footer.Children.Add(notice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(save);
        buttons.Children.Add(copy);
        buttons.Children.Add(InsertButton);
        buttons.Children.Add(new Button { Content = "关闭", IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) });
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        foreach (var field in new[] { ClockField, DelayField, BaudField, NameField })
        {
            field.TextChanged += (_, _) => Refresh();
        }
        foreach (var picker in new[] { DividerPicker, TimerPicker, GeneratorPicker, PinsPicker })
        {
            picker.SelectionChanged += (_, _) => Refresh();
        }
        KindPicker.SelectionChanged += (_, _) =>
        {
            NameField.Text = KindPicker.SelectedIndex == 0 ? "sx_delay" : "sx_uart1_init";
            Refresh();
        };
        copy.Click += async (_, _) => await PerformAsync(async () =>
        {
            await service.ValidateAsync(Context);
            Refresh();
            if (PreviewResult is { CanInsert: true } preview)
            {
                Clipboard.SetText(preview.Code);
                notice.Text = "代码已复制；添加后请调用生成函数并检查返回值。";
            }
        });
        save.Click += async (_, _) => await PerformAsync(async () =>
        {
            Context = await service.SaveAsync(Context, ReadSettings());
            notice.Text = "生成参数已按工程保存；芯片时钟和下载设置未修改。";
        });
        InsertButton.Click += async (_, _) => await PerformAsync(async () =>
        {
            await service.ValidateAsync(Context);
            Refresh();
            if (PreviewResult is { CanInsert: true } preview && insertable)
            {
                Result = preview;
                DialogResult = true;
            }
        });
        ready = true;
        Refresh();

        void Add(string title, Control field)
        {
            fields.Children.Add(new TextBlock { Text = title, Margin = new(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
            field.Margin = new(0, 0, 0, 10);
            System.Windows.Automation.AutomationProperties.SetName(field, title);
            fields.Children.Add(field);
        }
    }

    private StcTimingSettings ReadSettings()
    {
        int Number(TextBox box) => int.Parse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
        return Context.Settings with
        {
            ReferenceClockHz = Context.ProjectClockHz is not null ? Context.Settings.ReferenceClockHz : Number(ClockField),
            TimerDivider = (int)DividerPicker.SelectedItem,
            DelayMicroseconds = Number(DelayField),
            DelayTimer = (int)TimerPicker.SelectedItem,
            BaudRate = Number(BaudField),
            BaudGenerator = (string)GeneratorPicker.SelectedItem,
            UartPins = (string)PinsPicker.SelectedItem
        };
    }

    private void Refresh()
    {
        if (!ready)
        {
            return;
        }
        var delay = KindPicker.SelectedIndex == 0;
        DelayField.IsEnabled = TimerPicker.IsEnabled = delay && !busy;
        BaudField.IsEnabled = GeneratorPicker.IsEnabled = PinsPicker.IsEnabled = !delay && !busy;
        try
        {
            var settings = ReadSettings();
            PreviewResult = delay ? service.GenerateDelay(Context, settings, NameField.Text.Trim()) : service.GenerateUart(Context, settings, NameField.Text.Trim());
            PreviewField.Text = PreviewResult.Code;
            SummaryField.Text = PreviewResult.Summary;
            notice.Text = PreviewResult.Notice + (!insertable ? " 打开可编辑 C 文件后可直接插入。" : "");
            InsertButton.IsEnabled = !busy && insertable && PreviewResult.CanInsert;
            copy.IsEnabled = !busy && PreviewResult.CanInsert;
            save.IsEnabled = !busy;
        }
        catch (Exception ex)
        {
            PreviewResult = null;
            PreviewField.Clear();
            SummaryField.Text = "参数尚未有效。";
            notice.Text = ex.Message;
            notice.ToolTip = ex.ToString();
            InsertButton.IsEnabled = copy.IsEnabled = save.IsEnabled = false;
        }
    }

    private async Task PerformAsync(Func<Task> operation)
    {
        busy = true;
        Refresh();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            notice.Text = ex.Message;
            notice.ToolTip = ex.ToString();
            log(ex.ToString());
        }
        finally
        {
            busy = false;
            InsertButton.IsEnabled = insertable && PreviewResult?.CanInsert == true;
            copy.IsEnabled = PreviewResult?.CanInsert == true;
            save.IsEnabled = PreviewResult is not null;
            DelayField.IsEnabled = TimerPicker.IsEnabled = KindPicker.SelectedIndex == 0;
            BaudField.IsEnabled = GeneratorPicker.IsEnabled = PinsPicker.IsEnabled = KindPicker.SelectedIndex != 0;
        }
    }
}

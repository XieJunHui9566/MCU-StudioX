namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.PeripheralDevelopment;

internal sealed class PeripheralDevelopmentWindow : Window
{
    internal ListBox Options { get; } = new() { DisplayMemberPath = "Name" };
    internal Dictionary<string, TextBox> Fields { get; } = new(StringComparer.Ordinal);
    internal TextBox Preview { get; } = TextView();
    internal TextBox Guidance { get; } = TextView();
    internal TextBox DependencyPreview { get; } = TextView();
    internal Expander DependencyDetails { get; } = new() { Header = "查看组件配置修改", Margin = new(0, 4, 0, 8) };
    internal Button Insert { get; } = new() { Content = "添加到工程", MinWidth = 130, Margin = new(8, 0, 0, 0) };
    internal TextBlock StatusLine { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly StackPanel parameters = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly PeripheralDevelopmentService service;
    private readonly PeripheralDevelopmentContext context;
    private readonly bool insertable;
    private readonly PeripheralComponentContext? component;
    private readonly string? componentIssue;
    private readonly Action<string> log;
    private bool validating;
    private bool ended;
    public PeripheralCodePreview? PreviewResult
    {
        get; private set;
    }
    public PeripheralProjectAddition? AdditionResult
    {
        get; private set;
    }

    public PeripheralDevelopmentWindow(PeripheralDevelopmentService service, PeripheralDevelopmentContext context, bool insertable, Action<string> log,
        PeripheralComponentContext? component = null, string? componentIssue = null)
    {
        this.service = service;
        this.context = context;
        this.insertable = insertable;
        this.log = log;
        this.component = component;
        this.componentIssue = componentIssue;
        Title = "外设开发辅助 · " + context.Project.DeviceId;
        Width = 1100;
        Height = 820;
        MinWidth = 780;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        Options.SetResourceReference(BackgroundProperty, "ToolSurface");
        Options.SetResourceReference(ForegroundProperty, "Text");
        Options.SetResourceReference(BorderBrushProperty, "Border");
        var optionStyle = new Style(typeof(ListBoxItem), (Style)FindResource("CodeCompletionItemStyle"));
        optionStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("Availability")));
        Options.ItemContainerStyle = optionStyle;
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
        var identity = new TextBlock { Text = $"{context.Project.DeviceId} · {context.Project.Espressif!.Target} · ESP-IDF {context.Project.Espressif.SdkVersion}\n填写板上 GPIO；初始化代码须在文件顶层插入。SDK 支持与板级引脚占用分别核对。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        root.Children.Add(identity);
        var content = new Grid();
        content.ColumnDefinitions.Add(new()
        {
            Width = new(255)
        });
        content.ColumnDefinitions.Add(new()
        {
            Width = new(12)
        });
        content.ColumnDefinitions.Add(new());
        var left = new Grid();
        left.RowDefinitions.Add(new()
        {
            Height = new(235)
        });
        left.RowDefinitions.Add(new());
        Options.ItemsSource = context.Options;
        Options.SelectionChanged += (_, _) => SelectOption();
        left.Children.Add(Options);
        var fields = new ScrollViewer { Content = parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 10, 0, 0) };
        Grid.SetRow(fields, 1);
        left.Children.Add(fields);
        content.Children.Add(left);
        var right = new Grid();
        right.RowDefinitions.Add(new());
        right.RowDefinitions.Add(new()
        {
            Height = new(200)
        });
        right.Children.Add(Preview);
        Grid.SetRow(Guidance, 1);
        Guidance.Margin = new(0, 10, 0, 0);
        Guidance.TextWrapping = TextWrapping.Wrap;
        Guidance.FontFamily = new("Microsoft YaHei UI");
        Guidance.FontSize = 13;
        right.Children.Add(Guidance);
        Grid.SetColumn(right, 2);
        content.Children.Add(right);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var footer = new StackPanel();
        DependencyPreview.Height = 145;
        DependencyDetails.Content = DependencyPreview;
        footer.Children.Add(DependencyDetails);
        footer.Children.Add(StatusLine);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "复制代码", MinWidth = 90 };
        copy.Click += (_, _) => Copy(PreviewResult?.Code);
        Insert.Click += async (_, _) => await ConfirmAsync();
        buttons.Children.Add(copy);
        buttons.Children.Add(Insert);
        buttons.Children.Add(new Button { Content = "关闭", IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) });
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Closed += (_, _) => { ended = true; lifetime.Cancel(); lifetime.Dispose(); };
        Options.SelectedIndex = 0;
    }

    private void SelectOption()
    {
        parameters.Children.Clear();
        Fields.Clear();
        if (Options.SelectedItem is not PeripheralOption option)
        {
            return;
        }
        foreach (var parameter in option.Parameters)
        {
            parameters.Children.Add(new TextBlock { Text = parameter.Label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4) });
            var box = new TextBox { Text = parameter.DefaultValue, MaxLength = 32, Margin = new(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(box, parameter.Label);
            Fields.Add(parameter.Id, box);
            parameters.Children.Add(box);
            box.TextChanged += (_, _) => RefreshPreview();
        }
        RefreshPreview();
    }

    internal void RefreshPreview()
    {
        if (Options.SelectedItem is not PeripheralOption option)
        {
            return;
        }
        AdditionResult = null;
        try
        {
            PreviewResult = service.Generate(context, option.Id, Fields.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal));
            Preview.Text = PreviewResult.Code;
            Guidance.Text = PreviewResult.Dependencies + "\n\n" + PreviewResult.Guidance + "\n官方文档：" + option.DocumentationUrl;
            DependencyPreview.Text = "需要的依赖：PRIV_REQUIRES " + string.Join(' ', PreviewResult.RequiredComponents);
            if (component is not null)
            {
                try
                {
                    AdditionResult = service.PrepareAddition(context, component, PreviewResult);
                    DependencyPreview.Text += "\n\n" + component.CMake.Source.RelativePath + " · 修改前\n" + component.CMake.Text +
                        "\n修改后\n" + AdditionResult.Files[1].After;
                    StatusLine.Text = AdditionResult.Summary + "\n代码和依赖一起加入未保存文件；保存全部后生效，可撤销上次外设添加。";
                }
                catch (Exception ex) { StatusLine.Text = ex.Message; StatusLine.ToolTip = ex.ToString(); }
            }
            else
            {
                StatusLine.Text = componentIssue ?? "可复制代码；打开此 ESP-IDF 工程内已参与编译的 C 源文件后可添加到工程。";
            }
            Insert.IsEnabled = insertable && AdditionResult is not null && !validating;
        }
        catch (Exception ex)
        {
            PreviewResult = null;
            Preview.Clear();
            DependencyPreview.Clear();
            Guidance.Text = $"#include \"{option.Header}\"\n组件：{option.Component}\n{option.Availability}\n{option.Notes}\n官方文档：{option.DocumentationUrl}";
            StatusLine.Text = ex.Message;
            StatusLine.ToolTip = ex.ToString();
            Insert.IsEnabled = false;
        }
    }

    private async Task ConfirmAsync()
    {
        if (validating || !insertable || PreviewResult is null || AdditionResult is null)
        {
            return;
        }
        validating = true;
        IsEnabled = false;
        try
        {
            await service.ValidateAsync(context, lifetime.Token);
            if (!ended)
            {
                RefreshPreview();
                if (AdditionResult is not null)
                {
                    DialogResult = true;
                }
            }
        }
        catch (OperationCanceledException) when (ended) { }
        catch (Exception ex) { if (!ended) { StatusLine.Text = ex.Message; StatusLine.ToolTip = ex.ToString(); PreviewResult = null; Insert.IsEnabled = false; } log(ex.ToString()); }
        finally { validating = false; if (!ended) { IsEnabled = true; } }
    }

    private void Copy(string? text)
    {
        if (text is null)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            StatusLine.Text = "已复制。";
        }
        catch (Exception ex) { StatusLine.Text = "复制失败：" + ex.Message; log(ex.ToString()); }
    }
    private static TextBox TextView()
    {
        var view = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        view.SetResourceReference(BackgroundProperty, "EditorSurface");
        view.SetResourceReference(ForegroundProperty, "Text");
        return view;
    }
}

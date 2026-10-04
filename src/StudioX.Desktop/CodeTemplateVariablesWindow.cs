namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.Editing;

internal sealed class CodeTemplateVariablesWindow : Window
{
    internal Dictionary<string, TextBox> Fields { get; } = new(StringComparer.Ordinal);
    internal TextBox Preview { get; } = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    public CodeTemplateExpansion? Expansion
    {
        get; private set;
    }
    public CodeTemplateVariablesWindow(CodeTemplate template, Func<IReadOnlyDictionary<string, string>, CodeTemplateExpansion> expand)
    {
        Title = "填写模板变量 · " + template.Name;
        Width = 850;
        Height = 680;
        MinWidth = 600;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto,
            MaxHeight = 230
        });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
        var insert = new Button { Content = "插入", IsDefault = true, MinWidth = 90, Margin = new(0, 0, 8, 0) };
        var fields = new StackPanel();
        foreach (var parameter in CodeTemplateExpander.Describe(template.Body))
        {
            fields.Children.Add(new TextBlock { Text = parameter.Name, Margin = new(0, 0, 0, 4) });
            var input = new TextBox { Text = parameter.DefaultValue, Margin = new(0, 0, 0, 8), MaxLength = CodeTemplateService.MaximumBodyLength };
            Fields.Add(parameter.Name, input);
            fields.Children.Add(input);
            System.Windows.Automation.AutomationProperties.SetName(input, parameter.Name);
            input.TextChanged += (_, _) => Refresh();
        }
        root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 10) });
        Grid.SetRow(Preview, 1);
        root.Children.Add(Preview);
        Preview.SetResourceReference(BackgroundProperty, "EditorSurface");
        Preview.SetResourceReference(ForegroundProperty, "Text");
        var footer = new StackPanel();
        footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        insert.Click += (_, _) => { Refresh(); if (Expansion is not null) { DialogResult = true; } };
        buttons.Children.Add(insert);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 80 });
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        Loaded += (_, _) => { Refresh(); if (Fields.Values.FirstOrDefault() is { } first) { first.Focus(); first.SelectAll(); } };
        void Refresh()
        {
            // 构造控件时不触发刷新；用户输入后仅做文本替换和预览。
            try
            {
                Expansion = expand(Fields.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal));
                Preview.Text = Expansion.Text;
                status.Text = "同名变量一起替换，插入后可一次撤销。";
                insert.IsEnabled = true;
            }
            catch (Exception ex) { Expansion = null; Preview.Clear(); status.Text = ex.Message; status.ToolTip = ex.ToString(); insert.IsEnabled = false; }
        }
    }
}

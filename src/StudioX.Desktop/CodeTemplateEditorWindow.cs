namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.Editing;

internal sealed class CodeTemplateEditorWindow : Window
{
    internal TextBox NameField { get; } = new() { MaxLength = 100 };
    internal TextBox ShortcutField { get; } = new() { MaxLength = 40 };
    internal TextBox BodyField { get; } = new() { AcceptsReturn = true, AcceptsTab = true, MaxLength = CodeTemplateService.MaximumBodyLength, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    internal ComboBox LanguageField { get; } = new() { ItemsSource = CodeTemplateService.Languages };
    private readonly ComboBox scopeField = new();
    private readonly TextBox descriptionField = new() { MaxLength = 1000 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private bool saving;
    public CodeTemplate? Saved { get; private set; }

    public CodeTemplateEditorWindow(CodeTemplateService service, string? project, CodeTemplateLibrary library, CodeTemplate template, CodeTemplateScope scope, bool editing, Action<string> log)
    {
        Title = editing ? "编辑代码模板" : "保存代码模板";
        Width = 900; Height = 750; MinWidth = 650; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Text");
        var root = new Grid { Margin = new(20) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var fields = new Grid(); fields.ColumnDefinitions.Add(new()); fields.ColumnDefinitions.Add(new());
        for (var index = 0; index < 3; index++) { fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        AddField("名称", NameField, 0, 0); AddField("缩写（可留空，输入后按 Ctrl+Space 调用）", ShortcutField, 0, 1);
        AddField("适用语言", LanguageField, 1, 0); AddField("保存位置", scopeField, 1, 1);
        AddField("说明", descriptionField, 2, 0); Grid.SetColumnSpan(fields.Children[^1], 2);
        root.Children.Add(fields);
        var body = new Grid { Margin = new(0, 10, 0, 10) }; body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
        body.Children.Add(new TextBlock { Text = "正文：${name:默认值} 填写变量 · ${cursor} 光标 · ${selection} 选区 · ${fileName}/${fileStem} 文件名 · $$ 普通 $", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        Grid.SetRow(BodyField, 1); body.Children.Add(BodyField); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new StackPanel(); footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var save = new Button { Content = "保存模板", IsDefault = true, MinWidth = 100, Margin = new(0, 0, 8, 0) };
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 80 }); footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        scopeField.ItemsSource = project is null ? new[] { "个人（所有工程可用）" } : new[] { "个人（所有工程可用）", "工程（随源码共享）" };
        scopeField.SelectedIndex = scope == CodeTemplateScope.Project ? 1 : 0; scopeField.IsEnabled = !editing;
        NameField.Text = template.Name; ShortcutField.Text = template.Shortcut; LanguageField.SelectedItem = template.Language; descriptionField.Text = template.Description; BodyField.Text = template.Body;
        BodyField.SetResourceReference(BackgroundProperty, "EditorSurface"); BodyField.SetResourceReference(ForegroundProperty, "Text");
        save.Click += async (_, _) =>
        {
            if (saving) { return; }
            saving = true; save.IsEnabled = false;
            try
            {
                var chosenScope = scopeField.SelectedIndex == 1 ? CodeTemplateScope.Project : CodeTemplateScope.User;
                var store = chosenScope == CodeTemplateScope.Project ? library.Project! : library.User;
                var updated = template with { Name = NameField.Text.Trim(), Shortcut = ShortcutField.Text.Trim(), Language = (string)LanguageField.SelectedItem, Description = descriptionField.Text.Trim(), Body = BodyField.Text };
                var items = store.Templates.Where(t => !editing || t.Id != template.Id).Append(updated).ToArray();
                await service.SaveAsync(project, chosenScope, store.Revision, items);
                Saved = updated; saving = false; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; status.ToolTip = ex.ToString(); log(ex.ToString()); }
            finally { saving = false; save.IsEnabled = true; }
        };
        Closing += (_, e) => { if (saving) { e.Cancel = true; } };
        Loaded += (_, _) => { NameField.Focus(); NameField.SelectAll(); };
        void AddField(string label, Control control, int row, int column)
        {
            var panel = new StackPanel { Margin = new(column == 0 ? 0 : 12, 0, 0, 10) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 5) }); panel.Children.Add(control);
            Grid.SetRow(panel, row); Grid.SetColumn(panel, column); fields.Children.Add(panel);
            System.Windows.Automation.AutomationProperties.SetName(control, label);
        }
    }
}

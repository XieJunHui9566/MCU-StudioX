namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application.Editing;

/// <summary>列表与只读预览分开；编辑使用独立保存对话框，切换模板不会丢失草稿。</summary>
internal sealed class CodeTemplateWindow : Window
{
    internal TextBox Query { get; } = new() { Margin = new(0, 0, 0, 10) };
    internal ListBox Templates { get; } = new();
    internal TextBox Preview { get; } = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly ComboBox language = new() { Margin = new(0, 0, 0, 10) };
    private CodeTemplateLibrary library;
    private bool busy;
    public CodeTemplateEntry? Selected => Templates.SelectedItem as CodeTemplateEntry;

    public CodeTemplateWindow(CodeTemplateService service, string? project, CodeTemplateLibrary library, string? currentLanguage, bool allowInsert, Action<string> log)
    {
        this.library = library;
        Title = "代码模板"; Width = 1100; Height = 760; MinWidth = 750; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(ForegroundProperty, "Text");
        var root = new Grid { Margin = new(20) }; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "双击或点击插入调用模板 · 选中源码后可从编辑菜单保存为模板 · Ctrl+Space 调用缩写", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new GridLength(330) }); content.ColumnDefinitions.Add(new());
        var left = new Grid { Margin = new(0, 0, 16, 0) }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new());
        left.Children.Add(new TextBlock { Text = "搜索名称 / 缩写 / 说明", Margin = new(0, 0, 0, 5) });
        Query.ToolTip = "搜索名称、缩写或说明"; Grid.SetRow(Query, 1); left.Children.Add(Query); Grid.SetRow(language, 2); left.Children.Add(language); Grid.SetRow(Templates, 3); left.Children.Add(Templates); content.Children.Add(left);
        System.Windows.Automation.AutomationProperties.SetName(Query, "搜索代码模板"); System.Windows.Automation.AutomationProperties.SetName(Templates, "代码模板列表");
        var right = new Grid(); right.RowDefinitions.Add(new() { Height = GridLength.Auto }); right.RowDefinitions.Add(new()); right.Children.Add(detail); Grid.SetRow(Preview, 1); right.Children.Add(Preview); Grid.SetColumn(right, 1); content.Children.Add(right); Grid.SetRow(content, 1); root.Children.Add(content);
        var footer = new StackPanel(); footer.Children.Add(status);
        var buttons = new WrapPanel { Margin = new(0, 12, 0, 0) };
        var create = Button("新建…"); var edit = Button("编辑…"); var copy = Button("复制…"); var delete = Button("删除…"); var insert = Button("插入选中模板"); var close = Button("关闭"); close.IsCancel = true;
        foreach (var button in new[] { create, edit, copy, delete, insert, close }) { buttons.Children.Add(button); }
        footer.Children.Add(buttons); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        Preview.SetResourceReference(BackgroundProperty, "EditorSurface"); Preview.SetResourceReference(ForegroundProperty, "Text"); Templates.SetResourceReference(BackgroundProperty, "ToolSurface"); Templates.SetResourceReference(ForegroundProperty, "Text");
        language.ItemsSource = new[] { "所有语言" }.Concat(CodeTemplateService.Languages).ToArray(); language.SelectedItem = currentLanguage ?? "所有语言";
        Query.TextChanged += (_, _) => Refresh(); language.SelectionChanged += (_, _) => Refresh();
        Templates.SelectionChanged += (_, _) => Describe();
        Templates.MouseDoubleClick += (_, _) => Accept();
        Templates.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        insert.Click += (_, _) => Accept();
        create.Click += async (_, _) => await EditAsync(false, false);
        edit.Click += async (_, _) => await EditAsync(true, false);
        copy.Click += async (_, _) => await EditAsync(false, true);
        delete.Click += async (_, _) =>
        {
            if (Selected is not { Scope: not CodeTemplateScope.BuiltIn } entry || busy) { return; }
            if (MessageBox.Show(this, "删除模板“" + entry.Template.Name + "”？", "删除代码模板", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { return; }
            await ChangeAsync(async () =>
            {
                var store = entry.Scope == CodeTemplateScope.Project ? this.library.Project! : this.library.User;
                await service.SaveAsync(project, entry.Scope, store.Revision, store.Templates.Where(t => t.Id != entry.Template.Id).ToArray());
            });
        };
        Closing += (_, e) => { if (busy) { e.Cancel = true; } };
        Refresh();
        void Accept() { if (allowInsert && !busy && Selected is not null) { DialogResult = true; } }
        void Describe()
        {
            var entry = Selected; var template = entry?.Template;
            Preview.Text = template?.Body ?? "";
            detail.Text = template is null ? "选择一个模板查看正文。" : $"{template.Name}  ·  {entry!.ScopeLabel}  ·  {template.Language}\n{template.Description}\n缩写：{(template.Shortcut.Length == 0 ? "未设置" : template.Shortcut)}";
            edit.IsEnabled = delete.IsEnabled = !busy && entry is { Scope: not CodeTemplateScope.BuiltIn };
            copy.IsEnabled = !busy && entry is not null; insert.IsEnabled = !busy && allowInsert && entry is not null;
        }
        async Task EditAsync(bool editing, bool copying)
        {
            if (busy || (editing || copying) && Selected is null) { return; }
            var source = editing || copying ? Selected!.Template : new CodeTemplate(Guid.NewGuid().ToString("N"), "", "", currentLanguage ?? "全部", "", "${cursor}");
            if (!editing) { source = source with { Id = Guid.NewGuid().ToString("N"), Name = copying ? source.Name + "（副本）" : source.Name, Shortcut = copying ? "" : source.Shortcut }; }
            var scope = editing ? Selected!.Scope : CodeTemplateScope.User;
            var editor = new CodeTemplateEditorWindow(service, project, this.library, source, scope, editing, log) { Owner = this };
            if (editor.ShowDialog() == true) { await ChangeAsync(() => Task.CompletedTask, editor.Saved?.Id); }
        }
        async Task ChangeAsync(Func<Task> change, string? selectedId = null)
        {
            busy = true; create.IsEnabled = false; Describe();
            try { await change(); this.library = await service.LoadAsync(project); Refresh(); if (selectedId is not null) { Templates.SelectedItem = Templates.Items.OfType<CodeTemplateEntry>().FirstOrDefault(e => e.Template.Id == selectedId); } }
            catch (Exception ex) { status.Text = ex.Message; status.ToolTip = ex.ToString(); log(ex.ToString()); }
            finally { busy = false; create.IsEnabled = true; Describe(); }
        }
        void Refresh()
        {
            var previous = Selected;
            var query = Query.Text.Trim(); var filter = language.SelectedItem as string ?? "所有语言";
            Templates.ItemsSource = this.library.Entries.Where(e => (filter == "所有语言" || filter == "全部" && e.Template.Language == "全部" || CodeTemplateService.Supports(e.Template, filter)) &&
                (query.Length == 0 || (e.Template.Name + " " + e.Template.Shortcut + " " + e.Template.Description + " " + e.ScopeLabel).Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.Scope).ThenBy(e => e.Template.Name).ToArray();
            Templates.SelectedItem = Templates.Items.OfType<CodeTemplateEntry>().FirstOrDefault(e => e == previous);
            if (Templates.SelectedItem is null && Templates.Items.Count > 0) { Templates.SelectedIndex = 0; }
            status.Text = $"{Templates.Items.Count} 个模板 · 内置模板可复制后修改\n个人：{this.library.User.Path}" + (this.library.Project is null ? "" : "\n工程：" + this.library.Project.Path);
            Describe();
        }
    }
    private static Button Button(string label) => new() { Content = label, MinWidth = 80, Margin = new(0, 0, 8, 0) };
}

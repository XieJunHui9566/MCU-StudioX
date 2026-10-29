namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.Plugins;
using StudioX.Extensions.Abstractions;

/// <summary>通用封装/时钟/表单视图；仅呈现已校验数据，将操作交回隔离插件。</summary>
internal sealed partial class PluginMcuConfigurationView : Border
{
    private readonly Func<JsonElement, Task> invoke;
    private readonly Action<string> diagnostic;
    private ContentControl? pageHost;
    private StackPanel? navigation;
    private TextBlock? pendingNotice;
    internal int Page
    {
        get; private set;
    }
    private bool busy;
    private readonly Dictionary<string, Func<string>> fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> expandedCategories = new(StringComparer.Ordinal);
    internal PluginMcuConfigurationState State
    {
        get; private set;
    }
    internal bool HasChanges => State.Dirty || HasUnappliedFields();

    internal string DraftIdentity()
    {
        return JsonSerializer.Serialize(new
        {
            State,
            Pending = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal)
        });
    }

    internal PluginMcuConfigurationView(JsonElement value, Func<JsonElement, Task> invoke, Action<string> diagnostic)
    {
        this.invoke = invoke;
        this.diagnostic = diagnostic;
        State = PluginMcuConfiguration.Parse(value);
        Padding = new Thickness(12);
        clockUpdateTimer.Tick += async (_, _) => await ApplyClockFieldsAsync();
        Loaded += (_, _) => QueueClockUpdate(typing: false);
        Unloaded += (_, _) =>
        {
            clockUpdateTimer.Stop();
            ClosePinMenu();
        };
        Update(value);
    }

    internal void Update(JsonElement value)
    {
        var focus = CaptureFieldFocus() ?? submittedFieldFocus;
        clockUpdateTimer.Stop();
        stateRevision++;
        ClosePinMenu(retainRequest: true);
        State = PluginMcuConfiguration.Parse(value);
        fields.Clear();
        var root = new StackPanel();
        root.Children.Add(Text(State.Title, 22, true));
        root.Children.Add(Text(State.Subtitle, 12));
        var toolbar = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        toolbar.Children.Add(Button("重新读取", () => Send(new { action = "reload" })));
        toolbar.Children.Add(Button("检查配置", () => Send(new { action = "validate" })));
        var save = Button(State.Dirty ? "保存配置 ●" : "保存配置", () => Send(new { action = "save" }));
        save.IsEnabled = State.CanSave;
        toolbar.Children.Add(save);
        var generate = Button("生成代码", () => Send(new { action = "generate" }));
        generate.IsEnabled = State.CanGenerate && State.CanSave;
        toolbar.Children.Add(generate);
        root.Children.Add(toolbar);
        pendingNotice = Text("", 12);
        pendingNotice.Visibility = Visibility.Collapsed;
        root.Children.Add(pendingNotice);
        root.Children.Add(Text(State.Status, 12));
        foreach (var message in State.Diagnostics)
        {
            var row = new Border { Background = new SolidColorBrush(Color.FromArgb(24, 230, 155, 45)), Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 0), Child = Text(message, 12) };
            root.Children.Add(row);
        }
        var workspace = new Grid { Margin = new Thickness(0, 12, 0, 0), MinHeight = 600 };
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        navigation = new StackPanel { Orientation = Orientation.Horizontal };
        var titles = new[] { "引脚与外设", "时钟树" };
        for (var index = 0; index < titles.Length; index++)
        {
            var target = index;
            var button = Button(titles[index], async () =>
            {
                if (HasUnappliedFields())
                {
                    await Send(new
                    {
                        action = "parameters",
                        values = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal)
                    });
                }
                SelectPage(target);
            });
            button.Tag = index;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 8, 8);
            navigation.Children.Add(button);
        }
        pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(pageHost, 1);
        workspace.Children.Add(navigation);
        workspace.Children.Add(pageHost);
        root.Children.Add(workspace);
        Child = root;
        RenderPage();
        RestoreFieldFocus(focus, preserveText: submittedFieldFocus is not null);
        QueuePinMenu();
    }

    internal void SelectPage(int page)
    {
        if (page != Page && HasUnappliedFields())
        {
            WarnPending();
            return;
        }
        ClosePinMenu();
        Page = Math.Clamp(page, 0, 1);
        RenderPage();
    }

    private void RenderPage()
    {
        clockUpdateTimer.Stop();
        fields.Clear();
        fieldControls.Clear();
        acceptedFieldText.Clear();
        if (pageHost is not null)
        {
            pageHost.Content = Page switch
            {
                0 => Pins(),
                _ => Parameters()
            };
        }
        if (navigation is not null)
        {
            foreach (var button in navigation.Children.OfType<Button>())
            {
                button.SetResourceReference(Control.BackgroundProperty, (int)button.Tag == Page ? "Accent" : "Surface");
                button.SetResourceReference(Control.ForegroundProperty, (int)button.Tag == Page ? "OnAccent" : "Text");
            }
        }
    }


    private void AddFieldGroups(StackPanel body, string page)
    {
        foreach (var group in State.Fields.Where(field => field.Page == page && field.ClockNodeId.Length == 0).GroupBy(field => field.Section))
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 6, 14), MaxWidth = 850, HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            foreach (var field in group)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Text(field.Label, 12);
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row++;
                var control = CreateFieldControl(field);
                Grid.SetColumn(control, 0);
                Grid.SetRow(control, row++);
                grid.Children.Add(control);
            }
            if (page == "peripherals")
            {
                body.Children.Add(Category(group.Key, grid, group.Any(field => field.Id.StartsWith("gpio", StringComparison.Ordinal))));
            }
            else
            {
                body.Children.Add(Text(group.Key, 15, true));
                body.Children.Add(grid);
            }
        }
        body.Children.Add(Button("应用参数并检查", () => Send(new { action = "parameters", values = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal) })));
    }

    private FrameworkElement CreateFieldControl(PluginMcuField field)
    {
        FrameworkElement control;
        if (field.Kind is "select" or "editableSelect")
        {
            var editable = field.Kind == "editableSelect";
            var select = new ComboBox { Margin = new Thickness(0, 4, 0, 4), IsEditable = editable, IsTextSearchEnabled = !editable };
            foreach (var option in field.Options)
            {
                select.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
            }
            select.SelectedItem = select.Items.Cast<ComboBoxItem>().SingleOrDefault(item => (string)item.Tag == field.Value);
            if (editable)
            {
                TextSearch.SetTextPath(select, "Tag");
                select.Text = field.Value;
                fields[field.Id] = () => select.Text;
            }
            else
            {
                fields[field.Id] = () => (string)((ComboBoxItem)select.SelectedItem!).Tag;
            }
            control = select;
        }
        else
        {
            var input = new TextBox { Text = field.Value, MaxLength = 128, Margin = new Thickness(0, 4, 0, 4) };
            fields[field.Id] = () => input.Text;
            control = input;
        }
        AutomationProperties.SetName(control, field.Label);
        AutomationProperties.SetAutomationId(control, field.Id);
        TrackClockField(field, control);
        return control;
    }

    private Expander Category(string name, FrameworkElement content, bool expanded = false)
    {
        var category = new Expander { Header = name, Content = content, IsExpanded = expandedCategories.GetValueOrDefault(name, expanded), Margin = new Thickness(0, 4, 0, 8) };
        category.SetResourceReference(Control.ForegroundProperty, "Text");
        category.Expanded += (_, _) => expandedCategories[name] = true;
        category.Collapsed += (_, _) => expandedCategories[name] = false;
        return category;
    }

    private async Task Send(object arguments)
    {
        if (busy || !IsEnabled)
        {
            return;
        }
        var payload = JsonSerializer.SerializeToElement(arguments);
        if (HasUnappliedFields() && payload.GetProperty("action").GetString() != "parameters")
        {
            WarnPending();
            return;
        }
        clockUpdateTimer.Stop();
        var parameters = payload.GetProperty("action").GetString() == "parameters";
        var revision = stateRevision;
        submittedFieldFocus = parameters ? CaptureFieldFocus() : null;
        busy = true;
        IsEnabled = false;
        try
        {
            await invoke(payload);
            if (parameters && stateRevision == revision && HasUnappliedFields())
            {
                WarnClockUpdate();
            }
        }
        catch (Exception error)
        {
            diagnostic(error.ToString());
            if (parameters)
            {
                WarnClockUpdate();
            }
        }
        finally
        {
            busy = false;
            IsEnabled = true;
            RestoreFieldFocus(submittedFieldFocus, preserveText: false);
            submittedFieldFocus = null;
        }
    }

    private bool HasUnappliedFields() => fields.Any(item => acceptedFieldText.GetValueOrDefault(item.Key, State.Fields.First(field => field.Id == item.Key).Value) != item.Value());
    private void WarnPending()
    {
        if (pendingNotice is not null)
        {
            pendingNotice.Text = "参数正在更新，请稍候；若输入无效，请先修正后再切换页面或保存。";
            pendingNotice.Visibility = Visibility.Visible;
        }
    }

    private static Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static TextBlock Text(string value, double size, bool bold = false) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 4)
    };
}

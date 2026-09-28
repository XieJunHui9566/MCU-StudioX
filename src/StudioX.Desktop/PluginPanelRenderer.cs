namespace StudioX.Desktop;

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Extensions.Abstractions;

/// <summary>把有界 JSON 控件数据映射为宿主控件；不解释 XAML、HTML 或插件界面代码。</summary>
internal sealed class PluginPanelRenderer(Func<string, JsonElement, Task> invoke, Action<string> diagnostic)
{
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> numbers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBox> inputs = new(StringComparer.Ordinal);
    private bool enabled = true;
    private FrameworkElement? current;

    public FrameworkElement Render(PluginPanelDefinition panel)
    {
        var declaredInputs = new HashSet<string>(StringComparer.Ordinal);
        ValidateKinds(panel.Widgets, 0, declaredInputs);
        foreach (var stale in values.Keys.Where(id => !declaredInputs.Contains(id)).ToArray())
        {
            values.Remove(stale);
        }
        var focused = inputs.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin);
        var selectionStart = focused.Value?.SelectionStart ?? 0;
        var selectionLength = focused.Value?.SelectionLength ?? 0;
        numbers.Clear();
        inputs.Clear();
        var body = new StackPanel();
        body.Children.Add(Text(panel.Title, heading: true));
        foreach (var widget in panel.Widgets)
        {
            body.Children.Add(RenderWidget(widget, 0));
        }
        var border = new Border
        {
            Child = body,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = enabled
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        if (focused.Key is not null && inputs.TryGetValue(focused.Key, out var nextInput))
        {
            // 发布的新统计不应打断正在输入的表单；控件替换后恢复同一个字段的光标。
            border.Loaded += (_, _) =>
            {
                if (enabled)
                {
                    nextInput.Focus();
                    nextInput.Select(Math.Min(selectionStart, nextInput.Text.Length), Math.Min(selectionLength, Math.Max(0, nextInput.Text.Length - selectionStart)));
                }
            };
        }
        current = border;
        return border;
    }

    private static void ValidateKinds(PluginPanelWidget[] widgets, int depth, HashSet<string> declaredInputs)
    {
        if (depth > 12)
        {
            throw new InvalidDataException("插件面板嵌套过深。");
        }
        foreach (var widget in widgets)
        {
            if (widget.Kind is not ("text" or "metric" or "table" or "tree" or "form" or "group" or
                "plot" or "chart" or "button" or "input" or "number" or "checkbox" or "select"))
            {
                throw new InvalidDataException("不支持的插件控件：" + widget.Kind);
            }
            if (widget.Kind is "input" or "number" or "checkbox" or "select")
            {
                declaredInputs.Add(widget.Id);
            }
            ValidateKinds(widget.Children ?? [], depth + 1, declaredInputs);
        }
    }

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (current is not null)
        {
            current.IsEnabled = value;
        }
    }

    private FrameworkElement RenderWidget(PluginPanelWidget widget, int depth)
    {
        if (depth > 12)
        {
            throw new InvalidDataException("插件面板嵌套过深。");
        }
        var kind = widget.Kind;
        if (kind is "form" or "group")
        {
            var group = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            group.Children.Add(Text(widget.Label, heading: true));
            foreach (var child in widget.Children ?? [])
            {
                group.Children.Add(RenderWidget(child, depth + 1));
            }
            return group;
        }
        if (kind == "tree")
        {
            var tree = new TreeView { MaxHeight = 260, Background = System.Windows.Media.Brushes.Transparent };
            tree.Items.Add(TreeNode(widget, depth));
            return tree;
        }
        if (kind == "table")
        {
            return Table(widget);
        }
        if (kind is "plot" or "chart")
        {
            var group = new StackPanel();
            group.Children.Add(Text(widget.Label));
            group.Children.Add(new PluginPlotView(widget.Value) { Height = 190, Margin = new Thickness(0, 6, 0, 8) });
            return group;
        }
        if (kind == "button")
        {
            var button = new Button { Content = widget.Label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += async (_, _) => await InvokeButtonAsync(widget);
            return button;
        }
        if (kind == "checkbox")
        {
            if (!values.ContainsKey(widget.Id))
            {
                values[widget.Id] = widget.Value?.ValueKind == JsonValueKind.True;
            }
            var check = new CheckBox { Content = widget.Label, IsChecked = values[widget.Id] as bool? ?? false, Margin = new Thickness(0, 8, 0, 0) };
            check.Checked += (_, _) => values[widget.Id] = true;
            check.Unchecked += (_, _) => values[widget.Id] = false;
            return check;
        }
        if (kind == "select")
        {
            return Select(widget);
        }
        if (kind is "input" or "number")
        {
            if (!values.ContainsKey(widget.Id))
            {
                values[widget.Id] = ValueText(widget.Value);
            }
            var input = new TextBox { Text = Convert.ToString(values[widget.Id], CultureInfo.InvariantCulture) ?? "", MaxLength = 4096, Margin = new Thickness(0, 4, 0, 0) };
            inputs[widget.Id] = input;
            input.TextChanged += (_, _) => values[widget.Id] = input.Text;
            if (kind == "number")
            {
                numbers[widget.Id] = input;
            }
            return Labeled(widget.Label, input);
        }
        if (kind is "text" or "metric")
        {
            return Text(widget.Value is null ? widget.Label : widget.Label + "  " + ValueText(widget.Value), heading: kind == "metric");
        }
        throw new InvalidDataException("不支持的插件控件：" + kind);
    }

    private async Task InvokeButtonAsync(PluginPanelWidget widget)
    {
        if (!enabled || widget.CommandId is not { Length: > 0 } command)
        {
            return;
        }
        try
        {
            var submitted = new Dictionary<string, object?>(values, StringComparer.Ordinal);
            foreach (var (id, input) in numbers)
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    throw new ArgumentException("请输入有效数值：" + id);
                }
                submitted[id] = number;
            }
            await invoke(command, JsonSerializer.SerializeToElement(new
            {
                widgetId = widget.Id,
                values = submitted
            }));
        }
        catch (Exception error)
        {
            diagnostic(error.ToString());
        }
    }

    private FrameworkElement Select(PluginPanelWidget widget)
    {
        var options = new List<PluginSelectOption>();
        var selected = "";
        var value = widget.Value;
        if (value is { ValueKind: JsonValueKind.Object } objectValue)
        {
            if (objectValue.TryGetProperty("selected", out var current))
            {
                selected = ValueText(current);
            }
            if (objectValue.TryGetProperty("options", out var list))
            {
                value = list;
            }
        }
        if (value is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var option in array.EnumerateArray().Take(100))
            {
                if (option.ValueKind == JsonValueKind.Object && option.TryGetProperty("value", out var raw))
                {
                    options.Add(new PluginSelectOption(option.TryGetProperty("label", out var label) ? ValueText(label) : ValueText(raw), raw.Clone()));
                }
                else
                {
                    options.Add(new PluginSelectOption(ValueText(option), option.Clone()));
                }
            }
        }
        var picker = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(PluginSelectOption.Label), Margin = new Thickness(0, 4, 0, 0) };
        var previous = values.TryGetValue(widget.Id, out var saved) ? Convert.ToString(saved, CultureInfo.InvariantCulture) ?? selected : selected;
        picker.SelectedItem = options.FirstOrDefault(option => ValueText(option.Value) == previous) ?? options.FirstOrDefault();
        if (picker.SelectedItem is PluginSelectOption initial)
        {
            values[widget.Id] = initial.Value;
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is PluginSelectOption choice)
            {
                values[widget.Id] = choice.Value;
            }
        };
        return Labeled(widget.Label, picker);
    }

    private static FrameworkElement Table(PluginPanelWidget widget)
    {
        var rows = widget.Value is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().Take(60).ToArray() : [];
        var columns = widget.Columns?.Take(12).ToArray() ??
            (rows.FirstOrDefault().ValueKind == JsonValueKind.Object ? rows[0].EnumerateObject().Take(12).Select(item => item.Name).ToArray() : []);
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        for (var column = 0; column < Math.Max(1, columns.Length); column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        AddRow(columns, 0, true);
        for (var row = 0; row < rows.Length; row++)
        {
            var item = rows[row];
            var cells = item.ValueKind == JsonValueKind.Array
                ? item.EnumerateArray().Take(12).Select(cell => ValueText(cell)).ToArray()
                : columns.Select(name => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var cell) ? ValueText(cell) : "").ToArray();
            AddRow(cells, row + 1, false);
        }
        var body = new StackPanel();
        body.Children.Add(Text(widget.Label));
        body.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (widget.Value is { ValueKind: JsonValueKind.Array } all && all.GetArrayLength() > rows.Length)
        {
            body.Children.Add(Text($"已显示 {rows.Length}/{all.GetArrayLength()} 行。"));
        }
        return body;

        void AddRow(string[] cells, int row, bool heading)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < Math.Min(cells.Length, grid.ColumnDefinitions.Count); column++)
            {
                var cell = Text(cells[column], heading);
                cell.Margin = new Thickness(6, 5, 10, 5);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
    }

    private static TreeViewItem TreeNode(PluginPanelWidget widget, int depth)
    {
        if (depth > 12)
        {
            throw new InvalidDataException("插件树嵌套过深。");
        }
        var node = new TreeViewItem { Header = widget.Label, IsExpanded = depth < 2 };
        foreach (var child in widget.Children ?? [])
        {
            node.Items.Add(TreeNode(child, depth + 1));
        }
        return node;
    }

    private static StackPanel Labeled(string label, FrameworkElement content)
    {
        var group = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        group.Children.Add(Text(label));
        group.Children.Add(content);
        return group;
    }

    private static TextBlock Text(string value, bool heading = false)
    {
        var result = new TextBlock
        {
            Text = value.Length <= 4096 ? value : value[..4096] + "…",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, 4, 0, 0)
        };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        return result;
    }

    private static string ValueText(JsonElement? value) => value is null ? "" : value.Value.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.Value.GetRawText()
    };

    private sealed record PluginSelectOption(string Label, JsonElement Value);
}

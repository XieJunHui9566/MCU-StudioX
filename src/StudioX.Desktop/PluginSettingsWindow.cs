namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Extensions.Abstractions;

internal sealed class PluginSettingsWindow : Window
{
    private readonly Dictionary<PluginSettingDefinition, Control> editors = [];
    public PluginSettingsWindow(string title, PluginSettingDefinition[] definitions, IReadOnlyDictionary<string, JsonElement> values)
    {
        Title = title;
        Width = 580;
        Height = 480;
        MinWidth = 400;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var save = new Button { Content = "保存设置", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(save, Dock.Bottom);
        root.Children.Add(save);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var rows = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var definition in definitions)
        {
            rows.Children.Add(new TextBlock { Text = definition.Title, Margin = new Thickness(0, 10, 0, 4) });
            Control editor = definition.Type == "boolean" ? new CheckBox { IsChecked = values[definition.Id].GetBoolean(), Content = definition.Description } : new TextBox { Text = definition.Type == "string" ? values[definition.Id].GetString() : values[definition.Id].GetRawText() };
            System.Windows.Automation.AutomationProperties.SetName(editor, definition.Title);
            rows.Children.Add(editor);
            editors.Add(definition, editor);
            if (definition.Type != "boolean")
            {
                rows.Children.Add(new TextBlock { Text = definition.Description, TextWrapping = TextWrapping.Wrap });
            }
        }
        save.Click += (_, _) =>
        {
            try
            {
                var result = new Dictionary<string, JsonElement>();
                foreach (var (definition, editor) in editors)
                {
                    var value = definition.Type switch
                    {
                        "boolean" => JsonSerializer.SerializeToElement(((CheckBox)editor).IsChecked == true),
                        "integer" => JsonSerializer.SerializeToElement(int.Parse(((TextBox)editor).Text, System.Globalization.CultureInfo.InvariantCulture)),
                        _ => JsonSerializer.SerializeToElement(((TextBox)editor).Text)
                    };
                    StudioX.Application.Plugins.PluginContributionValidator.ValidateSetting(definition, value);
                    result.Add(definition.Id, value);
                }
                Values = result;
                DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or StudioX.Foundation.StudioXException) { status.Text = ex.Message; }
        };
    }
    public IReadOnlyDictionary<string, JsonElement>? Values
    {
        get; private set;
    }
}

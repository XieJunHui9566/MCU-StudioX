namespace StudioX.Desktop;

using System.Windows;
using StudioX.Engine;

public partial class CubeMxImportWindow : Window
{
    private readonly bool hasPresets;
    public CubeMxImportWindow(CubeMxInspection inspection)
    {
        InitializeComponent();
        ProjectInfo.Text = inspection.Name + "  /  " + inspection.Device;
        ProjectPath.Text = inspection.Directory;
        hasPresets = inspection.ConfigurePresets.Count > 0;
        Configuration.ItemsSource = hasPresets ? inspection.ConfigurePresets : new[] { "Debug", "Release" };
        Configuration.SelectedItem = Configuration.Items.Cast<string>().FirstOrDefault(name => name.Equals("Debug", StringComparison.OrdinalIgnoreCase)) ?? Configuration.Items[0];
    }
    public string? ConfigurePreset => hasPresets ? (string)Configuration.SelectedItem : null;
    public string BuildType => hasPresets ? "Debug" : (string)Configuration.SelectedItem;
    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

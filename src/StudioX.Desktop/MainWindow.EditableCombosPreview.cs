namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderEditableCombosPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        ShowDocument(PackagesTab);
        NewProjectPanel.Visibility = Visibility.Collapsed;
        ProjectDetailsPanel.Visibility = Visibility.Visible;
        StcIspPanel.Visibility = Visibility.Visible;
        var capabilities = new StcIspCapabilities("IAP15F2K61S2",
            [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal], true, 5000000, 28000000, "离线界面夹具");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            ApplyStcIspSettings(new(Port: "COM14"), capabilities, new(false, null, null, "离线夹具"), ["COM4", "COM14"]);
            StcIspPanel.IsEnabled = true;
            StcPortPicker.ApplyTemplate();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var editor = StcPortPicker.Template.FindName("PART_EditableTextBox", StcPortPicker) as TextBox
                ?? throw new InvalidOperationException("STC port template has no editable text part.");
            Check(editor.Visibility == Visibility.Visible && editor.Text == "COM14",
                theme.Id + ": saved COM14 is displayed in editable STC port");
            StcPortPicker.SelectedItem = "COM4";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Check(StcPortPicker.Text == "COM4" && editor.Text == "COM4",
                theme.Id + ": selecting listed port displays selected text");
            editor.Text = "COM27";
            Check(StcPortPicker.Text == "COM27" && TrySelectedStcIspSettings(out var typed, out _) && typed.Port == "COM27",
                theme.Id + ": manual port input reaches download settings");
            var retained = StcPortPicker.Text;
            StcPortPicker.ItemsSource = new[] { "COM14", "COM27" };
            StcPortPicker.Text = retained;
            Check(editor.Text == "COM27" && StcPortPicker.Text == "COM27",
                theme.Id + ": refreshed port list retains text");
            ApplyStcIspSettings(new(Port: "COM14"), capabilities, new(false, null, null, "离线夹具"), ["COM14"]);
            StcIspPanel.IsEnabled = true;
            StcBaudPicker.ApplyTemplate();
            Check(StcBaudPicker.Template.FindName("PART_EditableTextBox", StcBaudPicker) is TextBox baudEditor &&
                baudEditor.Visibility == Visibility.Collapsed && StcBaudPicker.SelectedItem is 115200,
                theme.Id + ": ordinary baud selector retains selection presentation");
            StcPortPicker.BringIntoView();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check(editor.IsVisible && editor.ActualWidth > 0, theme.Id + ": port text is visible on the real WPF page");
            Render(this, Path.Combine(directory, "stc-port-" + theme.Id + ".png"));
        }
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            success = true,
            checks,
            hardware = false,
            openedPort = false
        });
    }
}

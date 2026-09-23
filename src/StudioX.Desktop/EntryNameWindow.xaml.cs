namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class EntryNameWindow : Window
{
    public string EntryName => NameInput.Text;
    public EntryNameWindow(string title, string prompt, string initial, bool directory)
    {
        InitializeComponent(); Title = title; Prompt.Text = prompt; NameInput.Text = initial;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            var extension = directory ? "" : Path.GetExtension(initial);
            NameInput.Select(0, initial.Length - extension.Length);
        };
    }
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try { ProjectFileService.ValidateEntryName(EntryName); DialogResult = true; }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; ErrorLabel.Visibility = Visibility.Visible; NameInput.Focus(); }
    }
}

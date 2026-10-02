namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;

public partial class MainWindow
{
    private TabItem? helpTab;
    private HelpCenterView? helpCenter;
    private Task ShowHelpAsync(string id = "quick-start")
    {
        if (helpTab is null || !WorkspaceTabs.Items.Contains(helpTab))
        {
            helpCenter = new HelpCenterView(services.Help);
            helpTab = AddToolTab("帮助中心", helpCenter);
        }
        helpCenter!.Open(id);
        ShowDocument(helpTab);
        return Task.CompletedTask;
    }
    private async void QuickStart_Click(object sender, RoutedEventArgs e) => await ShowHelpAsync();
}

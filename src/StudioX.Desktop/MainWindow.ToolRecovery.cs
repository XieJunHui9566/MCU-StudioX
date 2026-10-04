namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Tools;

public partial class MainWindow
{
    private async Task RecoverToolOperationAsync(CancellationToken token)
    {
        EnsureNoActiveDebug();
        var report = await services.ToolEnvironment.InspectRecoveryAsync(token);
        if (report.Diagnostics.Count > 0)
        {
            Log(string.Join('\n', report.Diagnostics));
        }
        if (report.Items.Count == 0)
        {
            await RefreshToolManagementAsync(token);
            return;
        }
        var panel = new DockPanel { Margin = new(16) };
        var actions = new Button { Content = "恢复所选操作", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 10, 0, 0), IsEnabled = false };
        var restorePrevious = new Button { Content = "恢复原组件", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(restorePrevious, Dock.Bottom);
        panel.Children.Add(restorePrevious);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 140, MaxHeight = 250, Margin = new(0, 10, 0, 0) };
        DockPanel.SetDock(detail, Dock.Bottom);
        panel.Children.Add(detail);
        var list = new ListBox { ItemsSource = report.Items, DisplayMemberPath = nameof(ToolRepairRecovery.Label) };
        var running = false;
        panel.Children.Add(list);
        var window = new Window
        {
            Owner = this,
            Title = "恢复未完成的组件安装或修复",
            Width = 760,
            Height = 580,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Surface"),
            Foreground = (System.Windows.Media.Brush)FindResource("Text")
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ToolRepairRecovery selected)
            {
                return;
            }
            detail.Text = selected.Detail;
            actions.Content = selected.ActionText;
            actions.IsEnabled = selected.CanRecover;
            restorePrevious.Visibility = selected.CanRestorePrevious ? Visibility.Visible : Visibility.Collapsed;
        };
        async Task RecoverSelectedAsync(bool usePrevious)
        {
            if (list.SelectedItem is not ToolRepairRecovery selected)
            {
                return;
            }
            if (MessageBox.Show(window, selected.Detail + "\n\n执行“" + (usePrevious ? "恢复原组件" : selected.ActionText) + "”？",
                "确认恢复范围", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
            actions.IsEnabled = restorePrevious.IsEnabled = list.IsEnabled = false;
            running = true;
            try
            {
                EnsureNoActiveDebug();
                var progress = new Progress<string>(text => detail.Text = text);
                if (usePrevious)
                {
                    await services.ToolEnvironment.RestorePreviousAsync(selected, progress, token);
                }
                else
                {
                    await services.ToolEnvironment.RecoverAsync(selected, progress, token);
                }
                Log("组件恢复完成：" + selected.Id + " / " + selected.Version);
                running = false;
                window.Close();
            }
            catch (Exception error) { detail.Text = FailureDiagnostic(error); Log(detail.Text); }
            finally { running = false; actions.IsEnabled = selected.CanRecover; restorePrevious.IsEnabled = true; list.IsEnabled = true; }
        }
        actions.Click += async (_, _) => await RecoverSelectedAsync(false);
        restorePrevious.Click += async (_, _) => await RecoverSelectedAsync(true);
        window.Closing += (_, args) => { if (running) { args.Cancel = true; } };
        list.SelectedIndex = 0;
        window.ShowDialog();
        await RefreshToolManagementAsync(token);
    }
}

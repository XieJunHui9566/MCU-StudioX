namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application.StcDebugging;

public partial class MainWindow
{
    private static Mon51FirmwareSource Mon51PreviewFirmware => new("stc.stc8", "0.1.2", "fixture-content",
        "IAP15F2K61S2", "stc.mon51.iap15f2k61s2", "2.5.0", StudioX.Engine.StcMonitorImage.SetupSha256, false);

    private async Task CheckMon51ConnectionRetryAsync(List<string> checks)
    {
        var attempts = 0;
        var connection = new Mon51ConnectionWindow(["COM14"], "COM14", "preview-project", [], (_, _, _, _, token) =>
            ++attempts == 1 ? throw new IOException("Fixture: monitor has no response") : Task.Delay(Timeout.Infinite, token),
            (_, _, _, _, _) => throw new IOException("Fixture: setup failed"))
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        connection.Show();
        try
        {
            var layout = (Grid)connection.Content;
            var body = (StackPanel)layout.Children.OfType<ScrollViewer>().Single().Content;
            var selectors = body.Children.OfType<ComboBox>().ToArray();
            selectors[0].SelectedIndex = 0;
            if (body.Children.OfType<Button>().Single().IsEnabled)
                {throw new InvalidOperationException("Missing package firmware must disable setup.");}
            checks.Add("missing monitor package disables setup while configured targets can still debug");
            body.Children.OfType<CheckBox>().Single().IsChecked = true;
            var controls = layout.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            connection.Width = connection.MinWidth;
            connection.Height = connection.MinHeight;
            connection.UpdateLayout();
            if (controls.Any(control =>
                control.TransformToAncestor(connection).TransformBounds(new Rect(control.RenderSize)).Bottom > connection.ActualHeight))
            {
                throw new InvalidOperationException("Connection actions are clipped at the minimum window size.");
            }
            checks.Add("minimum connection window keeps connect and cancel actions visible");
            controls[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!controls[0].IsEnabled || selectors.Any(selector => !selector.IsEnabled))
            {
                throw new InvalidOperationException("Connection failure did not restore retry controls.");
            }
            checks.Add("connection failure preserves raw diagnostic and restores parameter/retry controls");
            controls[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            controls[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!controls[0].IsEnabled)
            {
                await Task.Delay(20, deadline.Token);
            }
            if (attempts != 2 || selectors.Any(selector => !selector.IsEnabled))
            {
                throw new InvalidOperationException("Connection cancellation did not allow a fresh attempt.");
            }
            checks.Add("cancelled connection restores retry controls without cancelling window lifetime");
        }
        finally { connection.Close(); }
        var downloads = 0;
        var setups = 0;
        var downloadWindow = new Mon51ConnectionWindow(["COM14", "COM15"], "COM14", "preview-project",
            [Mon51PreviewFirmware],
            (_, _, _, _, _) => { downloads++; throw new IOException("Fixture: selected download failed before mutation"); },
            (_, _, source, _, _) =>
            {
                if (source != Mon51PreviewFirmware) {throw new InvalidOperationException("Selected package resource lost.");}
                setups++; return Task.CompletedTask;
            })
        {
            Owner = this, ShowActivated = false, ShowInTaskbar = false, Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual
        };
        downloadWindow.Show();
        try
        {
            var layout = (Grid)downloadWindow.Content;
            var body = (StackPanel)layout.Children.OfType<ScrollViewer>().Single().Content;
            if (body.Children.OfType<ComboBox>().ElementAt(2).SelectedItem is not Mon51FirmwareSource selected ||
                selected.PackVersion != "0.1.2" || body.Children.OfType<TextBlock>().Any(t => t.Text.Contains(".exe")))
                {throw new InvalidOperationException("Setup must select package firmware without a vendor executable prompt.");}
            checks.Add("setup displays the exact packaged firmware identity and requires no vendor executable");
            if (body.Children.OfType<RadioButton>().Any() || downloads != 0)
            {
                throw new InvalidOperationException("Debug entry must offer one workflow and wait for confirmed connection.");
            }
            checks.Add("debug entry has no attach/download mode selector and waits for confirmed connection");
            body.Children.OfType<ComboBox>().First().SelectedIndex = 0;
            body.Children.OfType<CheckBox>().Single().IsChecked = true;
            var start = layout.Children.OfType<StackPanel>().Single().Children.OfType<Button>().First();
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (downloads != 1 || !start.IsEnabled || (string)start.Content != "开始调试")
            {
                throw new InvalidOperationException("Selected download did not route to application action or allow retry.");
            }
            checks.Add("start debug always updates prepared user program and restores retry controls on failure");
            body.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var confirmation = body.Children.OfType<CheckBox>().Single();
            var detail = body.Children.OfType<TextBox>().Single();
            if (setups != 1 || downloads != 1 || confirmation.IsChecked == true || start.IsEnabled || !detail.Text.Contains("断电约 2 秒"))
                {throw new InvalidOperationException("Monitor setup must wait for power confirmation before starting or downloading.");}
            checks.Add("successful setup prompts a power cycle and never automatically downloads or starts debugging");
            confirmation.IsChecked = true;
            body.Children.OfType<ComboBox>().ElementAt(1).SelectedItem = "COM15";
            if (confirmation.IsChecked == true || start.IsEnabled)
                {throw new InvalidOperationException("Port changes must invalidate the power-cycle confirmation.");}
            checks.Add("changing the target port invalidates the previous power confirmation");
        }
        finally { downloadWindow.Close(); }
    }
}

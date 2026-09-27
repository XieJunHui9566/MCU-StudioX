namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>仅打开离线构建工程，预览设置与审批布局；不列举硬件、不点击写入。</summary>
    public async Task RenderEspressifPreviewAsync(string directory, string project)
    {
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }
        await BeginNewProjectAsync(CancellationToken.None);
        var vendor = VendorPicker.Items.Cast<ManufacturerOption>().Single(option =>
            option.Id.Equals("Espressif", StringComparison.OrdinalIgnoreCase));
        VendorPicker.SelectedItem = vendor;
        Check(vendor.Logo is not null && vendor.DisplayName == "乐鑫科技", "Espressif manufacturer mapping omitted official logo or localized name");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var visuals = Visuals(VendorPicker).ToArray();
            Check(visuals.OfType<Image>().Any(image => image.IsVisible && image.ActualWidth > 0 && ReferenceEquals(image.Source, vendor.Logo)) &&
                !visuals.OfType<TextBlock>().Any(text => text.IsVisible && text.Text == vendor.Monogram),
                "New project manufacturer picker did not render official Espressif logo");
            Render(this, Path.Combine(directory, "new-project-vendor-" + theme.Id + ".png"));
        }
        await OpenFromCommandLineAsync(project);
        Check(IsEspressifProject && DownloadButton.IsEnabled && DownloadProbePicker.Visibility == Visibility.Collapsed,
            "ESP project did not use dedicated serial download backend: " + Status.Text);
        Check(DebugStartButton.Visibility == Visibility.Collapsed, "ESP inherited unsupported OpenOCD debugging");
        var configuration = await services.EspressifDownloads.ConfigurationAsync(project)
            ?? throw new InvalidOperationException("Missing ESP configuration");
        var chosen = configuration.Settings with
        {
            Port = "COM12",
            BaudRate = 460800
        };
        var preview = await services.EspressifDownloads.PreviewAsync(project, chosen);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var memory = Visuals(BuildMemory).ToArray();
            var sdkDetails = memory.OfType<Expander>().Single(control => control.Name == "SdkDetails");
            Check(!sdkDetails.IsExpanded && memory.OfType<ItemsControl>().Single(control => control.Name == "VisibleRegions").Items.Count == 2,
                "ESP resources did not default to two totals with SDK details collapsed");
            Render(this, Path.Combine(directory, "workspace-" + theme.Id + ".png"));
            sdkDetails.IsExpanded = true;
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check(Visuals(BuildMemory).OfType<ItemsControl>().Single(control => control.Name == "SdkDetailRows").Items.Count > 0,
                "Expanded ESP resources omitted SDK region details");
            Render(this, Path.Combine(directory, "workspace-resources-detail-" + theme.Id + ".png"));
            sdkDetails.IsExpanded = false;
            string[] refreshedPorts = ["COM3", "COM12"];
            var settings = new EspressifDownloadWindow(configuration, ["COM7", "COM12"], () => Task.FromResult(refreshedPorts))
            {
                Owner = this,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -18000
            };
            settings.Show();
            settings.UpdateLayout();
            var portPicker = (ComboBox)settings.FindName("PortPicker");
            Check(portPicker.Text == configuration.Settings.Port,
                "Settings automatically selected an unapproved port");
            portPicker.IsDropDownOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var item = portPicker.ItemContainerGenerator.ContainerFromItem("COM12") as ComboBoxItem
                ?? throw new InvalidOperationException("WPF did not realize selectable port item");
            item.IsSelected = true;
            portPicker.IsDropDownOpen = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var portEditor = portPicker.Template.FindName("PART_EditableTextBox", portPicker) as TextBox
                ?? throw new InvalidOperationException("Editable COM picker omitted WPF text editor");
            Check(portEditor.IsVisible && portEditor.ActualWidth > 0 && portEditor.Text == "COM12" &&
                portPicker.Text == "COM12" && settings.ReadSettings().Port == "COM12", "Selected port disappeared from visible editor or save value");
            await settings.RefreshPortsAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check((string?)portPicker.SelectedItem == "COM12" && portEditor.Text == "COM12" && settings.ReadSettings().Port == "COM12",
                "Refreshing available ports lost matched selected COM");
            refreshedPorts = ["COM7"];
            await settings.RefreshPortsAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check(portPicker.SelectedItem is null && portEditor.Text == "COM12" && settings.ReadSettings().Port == "COM12",
                "Disconnected selected COM was cleared or replaced by refresh");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(settings, Path.Combine(directory, "settings-" + theme.Id + ".png"));
            portEditor.Text = "com35";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check(portPicker.Text == "com35" && settings.ReadSettings().Port == "COM35", "Manually typed COM was not synchronized to save value");
            refreshedPorts = ["COM35", "COM7"];
            await settings.RefreshPortsAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check((string?)portPicker.SelectedItem == "COM35" && portEditor.Text.Equals("com35", StringComparison.OrdinalIgnoreCase) &&
                settings.ReadSettings().Port == "COM35", "Reappeared manually typed COM was not restored on refresh");
            settings.Close();
            var review = new EspressifFlashReviewWindow(preview)
            {
                Owner = this,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -18000
            };
            review.Show();
            review.UpdateLayout();
            var details = ((TextBox)review.FindName("LayoutDetails")).Text;
            Check(preview.Layout.Images.All(image => details.Contains(image.Sha256, StringComparison.Ordinal)) &&
                details.Contains(preview.Layout.LayoutSha256, StringComparison.Ordinal), "Confirmation omitted image or layout hashes");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(review, Path.Combine(directory, "review-" + theme.Id + ".png"));
            review.Close();
        }
        await ShowProjectDetailsAsync();
        Check(projectDirectory == Path.GetFullPath(project) && CurrentProjectVendorLogo.Source is not null &&
            CurrentProjectVendorMonogram.Visibility == Visibility.Collapsed && CurrentProjectVendor.Text.Contains("乐鑫科技", StringComparison.Ordinal),
            "Current project details omitted Espressif logo or changed open project");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "project-details-" + theme.Id + ".png"));
        }
        UpdateProjectActions(true);
        Check(!DownloadButton.IsEnabled && !DownloadSettingsButton.IsEnabled, "ESP download remained enabled while busy");
        UpdateProjectActions(false);
        await CloseProjectAsync(CancellationToken.None);
        Check(!DownloadButton.IsEnabled && DownloadProbePicker.Items.Count == 0, "ESP project close leaked download state");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            "PASS: ESP toolbar, dark/light official manufacturer logos, editable COM selection visibly rendered and retained through refresh/disconnect/manual input with matching save value, no automatic COM selection, SDK layout/all hashes, busy/close lifecycle. No hardware accessed.\n");

        static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var descendant in Visuals(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

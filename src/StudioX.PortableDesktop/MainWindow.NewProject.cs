namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StudioX.Application;

internal sealed partial class MainWindow
{
    private async Task NewProjectAsync(CancellationToken token)
    {
        var packs = await workspace.PacksAsync(token);
        var dialog = new Window { Title = "新建工程", Width = 660, Height = 640, MinWidth = 600, MinHeight = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var vendor = new ComboBox { ItemsSource = packs.Select(pack => pack.Vendor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), PlaceholderText = "选择器件厂商" };
        var packBox = new ComboBox { IsEnabled = false, PlaceholderText = "选择器件包" };
        var device = new ComboBox { IsEnabled = false, PlaceholderText = "选择芯片型号" };
        var template = new ComboBox { IsEnabled = false, PlaceholderText = "选择起始模板" };
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 42 };
        var name = new TextBox { PlaceholderText = "工程名称" };
        var parent = new TextBox { PlaceholderText = "工程所在目录" };
        var browse = new Button { Content = "浏览…" };
        var import = new Button { Content = "导入器件包…" };
        var create = new Button { Content = "创建工程", IsEnabled = false };
        var cancel = new Button { Content = "取消" };
        void UpdateCreate()
        {
            create.IsEnabled = packBox.SelectedItem is PortablePackChoice && device.SelectedItem is PortableDeviceChoice
                && template.SelectedItem is PortableTemplateChoice { HostCompatible: true } && !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(parent.Text);
        }
        vendor.SelectionChanged += (_, _) =>
        {
            packBox.ItemsSource = packs.Where(pack => pack.Vendor == vendor.SelectedItem as string).ToArray();
            packBox.SelectedIndex = -1;
            packBox.IsEnabled = vendor.SelectedItem is string;
            device.ItemsSource = null;
            template.ItemsSource = null;
            device.IsEnabled = template.IsEnabled = false;
            description.Text = "";
            UpdateCreate();
        };
        packBox.SelectionChanged += (_, _) =>
        {
            device.ItemsSource = (packBox.SelectedItem as PortablePackChoice)?.Devices;
            device.SelectedIndex = -1;
            device.IsEnabled = packBox.SelectedItem is PortablePackChoice;
            template.ItemsSource = null;
            template.IsEnabled = false;
            description.Text = "";
            UpdateCreate();
        };
        device.SelectionChanged += (_, _) =>
        {
            template.ItemsSource = (device.SelectedItem as PortableDeviceChoice)?.Templates;
            template.SelectedIndex = -1;
            template.IsEnabled = device.SelectedItem is PortableDeviceChoice;
            description.Text = "";
            UpdateCreate();
        };
        template.SelectionChanged += (_, _) =>
        {
            description.Text = template.SelectedItem is PortableTemplateChoice selected ? selected.Description
                + (selected.HostCompatible ? "" : "\n此模板缺少当前平台的开发环境组件声明，请导入适配的器件包。") : "";
            UpdateCreate();
        };
        name.TextChanged += (_, _) => UpdateCreate();
        parent.TextChanged += (_, _) => UpdateCreate();
        browse.Click += async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择工程所在目录", AllowMultiple = false });
            if (!token.IsCancellationRequested && folders.Count == 1 && folders[0].TryGetLocalPath() is { } directory)
            {
                parent.Text = directory;
            }
        };
        cancel.Click += (_, _) => dialog.Close(new CreationSelection(null, null, null, "", "", false));
        import.Click += (_, _) => dialog.Close(new CreationSelection(null, null, null, "", "", true));
        create.Click += (_, _) =>
        {
            if (create.IsEnabled)
            {
                dialog.Close(new CreationSelection((PortablePackChoice)packBox.SelectedItem!, (PortableDeviceChoice)device.SelectedItem!,
                    (PortableTemplateChoice)template.SelectedItem!, name.Text!.Trim(), Path.Combine(parent.Text!.Trim(), name.Text!.Trim()), false));
            }
        };
        var destination = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        destination.Children.Add(parent);
        AddAt(destination, browse, 0, 1);
        var controls = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "新建工程", FontSize = 24, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "器件厂商 → 器件包 → 芯片型号 → 起始模板", Foreground = ColorBrush("Muted") },
                vendor, packBox, device, template, description, name, destination,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { import, cancel, create } }
            }
        };
        if (packs.Count == 0)
        {
            description.Text = "尚未安装器件包。先导入 StudioX 格式 1 包，再选择芯片和模板。";
        }
        dialog.Content = new ScrollViewer { Content = controls };
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        var selection = await dialog.ShowDialog<CreationSelection?>(this);
        token.ThrowIfCancellationRequested();
        if (selection?.Import == true)
        {
            await ImportPackAsync(token);
            return;
        }
        if (selection is { Pack: { } selectedPack, Device: { } selectedDevice, Template: { } selectedTemplate }
            && await ConfirmBuffersAsync(buffers, token))
        {
            await workspace.CreateAsync(selectedPack, selectedDevice.Id, selectedTemplate.Id, selection.Name, selection.Destination, token);
            await OpenProjectAsync(selection.Destination, token);
            await RefreshWelcomeAsync(token);
        }
    }

    private sealed record CreationSelection(PortablePackChoice? Pack, PortableDeviceChoice? Device, PortableTemplateChoice? Template,
        string Name, string Destination, bool Import);
}

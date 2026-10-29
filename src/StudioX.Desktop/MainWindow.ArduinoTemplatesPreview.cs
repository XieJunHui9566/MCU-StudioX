namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application.Arduino;
using StudioX.Engine.Arduino;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>验证统一创建页的框架切换、选择保留和跨芯片清除；不启动硬件会话。</summary>
    public async Task RenderArduinoTemplatesPreviewAsync(string directory, string avrArchive, string espArchives)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        void Save(string name)
        {
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            png.Save(file);
        }
        Width = 1500;
        Height = 1150;
        var avr = await services.Packs.ImportAsync(avrArchive);
        var esp = await services.Packs.ImportAsync(Path.Combine(espArchives, "espressif.esp32-wroom-32-0.1.1.mcupack"));
        var c3 = await services.Packs.ImportAsync(Path.Combine(espArchives, "espressif.esp32c3-0.1.1.mcupack"));
        await RefreshPacksAsync(CancellationToken.None);
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        SelectPack(esp);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        Check(TemplatePicker.Items.Cast<ProjectTemplate>().Select(item => item.Id).SequenceEqual(new[] { "hello-world", "freertos", ArduinoTemplateCatalog.TemplateId }), "ESP32 lists native SDK and Arduino in the same template picker");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(ArduinoTemplateCatalog.IsArduino);
        await Layout();
        Check(ArduinoTemplatePanel.IsVisible && IdfVersionPanel.Visibility == Visibility.Collapsed, "Arduino shows board selection and hides IDF version selection");
        Check(ArduinoBoardPicker.SelectedIndex == -1 && !CreateProjectButton.IsEnabled, "Arduino requires an explicit board configuration even with a single choice");
        ArduinoBoardPicker.SelectedItem = ArduinoBoardPicker.Items.Cast<ArduinoBoard>().Single();
        Check(CreateProjectButton.IsEnabled && SelectedDevelopmentComponents.Text.Contains("arduino.esp32 3.3.8", StringComparison.Ordinal), "ESP32 Arduino creation uses the Arduino component");
        await RefreshPacksAsync(CancellationToken.None, preserveSelection: true);
        Check((TemplatePicker.SelectedItem as ProjectTemplate)?.Id == ArduinoTemplateCatalog.TemplateId &&
            (ArduinoBoardPicker.SelectedItem as ArduinoBoard)?.Id == "esp32" && CreateProjectButton.IsEnabled, "catalog refresh preserves chip, Arduino template and board choice");
        foreach (var theme in new[] { StudioX.Application.ThemeService.Dark, StudioX.Application.ThemeService.Light })
        {
            ApplyTheme(theme);
            await Layout();
            Save(theme.Id + "-esp32-arduino-template.png");
        }
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(item => item.Id == "hello-world");
        await RefreshIdfVersionsAsync();
        await Layout();
        Check(ArduinoTemplatePanel.Visibility == Visibility.Collapsed && IdfVersionPanel.IsVisible && ArduinoBoardPicker.Items.Count == 0, "returning to IDF clears Arduino configuration and restores native SDK selection");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(ArduinoTemplateCatalog.IsArduino);
        ArduinoBoardPicker.SelectedIndex = 0;
        SelectPack(c3);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        Check(TemplatePicker.SelectedIndex == -1 && ArduinoBoardPicker.SelectedIndex == -1 && !CreateProjectButton.IsEnabled, "changing chip clears stale template and board choices");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(ArduinoTemplateCatalog.IsArduino);
        Check(ArduinoBoardPicker.Items.Cast<ArduinoBoard>().All(board => board.Id == "esp32c3"), "ESP32-C3 cannot select UNO or another ESP chip");
        SelectPack(avr);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(ArduinoTemplateCatalog.IsArduino);
        Check(ArduinoBoardPicker.Items.Count == 3 && !CreateProjectButton.IsEnabled, "ATmega328P offers UNO and both Nano bootloaders on the common page");
        ArduinoBoardPicker.SelectedItem = ArduinoBoardPicker.Items.Cast<ArduinoBoard>().Single(board => board.Id == "nano-old");
        await Layout();
        Check(CreateProjectButton.IsEnabled && SelectedDevelopmentComponents.Text.Contains("arduino.avr 1.8.8", StringComparison.Ordinal), "AVR retains its own exact component version");
        Save("avr-arduino-template.png");
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks
        });
    }
}

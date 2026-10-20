namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>真实 WPF 新建、编辑和编译验证；此入口不运行硬件工具。</summary>
    public async Task RenderPicPreviewAsync(string directory, string archive, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        async Task Save(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            png.Save(file);
        }
        Width = 1500;
        Height = 1120;
        var pack = await services.Packs.ImportAsync(archive);
        await RefreshPacksAsync(CancellationToken.None);
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        SelectPack(pack);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        Check(TemplatePicker.Items.Cast<ProjectTemplate>().Select(item => item.Id).SequenceEqual(new[] { "xc8-basic", "xc8-gpio-4mhz" }), "PIC templates use the common project picker");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(item => item.Id == "xc8-gpio-4mhz");
        Check(CreateProjectButton.IsEnabled && SelectedDevelopmentComponents.Text.Contains("microchip.xc8 4.0.0", StringComparison.Ordinal), "PIC selects the exact XC8 component");
        Check(ArduinoTemplatePanel.Visibility == Visibility.Collapsed && IdfVersionPanel.Visibility == Visibility.Collapsed, "PIC does not expose Arduino or IDF options");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Save(theme.Id + "-pic-template.png");
        }
        await OpenProjectAsync(project, CancellationToken.None);
        Check(currentProjectManifest?.Kind == ProjectKind.Pic && activeDocument?.RelativePath == "src/main.c", "PIC C opens in the real source editor");
        Check(services.Intelligence.IsReady, "PIC real clangd service starts");
        Check(BuildButton.IsEnabled && !DownloadButton.IsEnabled, "PIC compilation enabled while unimplemented hardware download stays disabled");
        Check(DebugStartButton.Visibility == Visibility.Collapsed && !CanEditBreakpoints, "unimplemented PIC debugging and breakpoints hidden");
        await ShowProjectDetailsAsync();
        Check(BuildSettingsCard.Visibility == Visibility.Collapsed, "PIC does not expose GCC/CMake build settings");
        var result = await BuildWithSummaryAsync(project, CancellationToken.None);
        Check(result.Success && result.Artifacts.Any(path => path.EndsWith(".hex", StringComparison.Ordinal)), "WPF build flow executes actual XC8 and presents HEX");
        Check(result.Log.Contains("2000h words", StringComparison.Ordinal), "PIC build output retains the proper program-word units");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Save(theme.Id + "-pic-workbench.png");
        }
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks
        });
    }
}

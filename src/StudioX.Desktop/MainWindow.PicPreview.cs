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
        Check(BuildButton.IsEnabled && DownloadButton.IsEnabled && DownloadProbePicker.Visibility == Visibility.Collapsed, "PIC16F877A build and dedicated PICkit3 download enabled without OpenOCD controls");
        Check(DownloadButton.ToolTip is string downloadHint && downloadHint.Contains("直接写入与校验", StringComparison.Ordinal) &&
            downloadHint.Contains("不备份", StringComparison.Ordinal), "PIC ordinary download describes direct programming without an original-firmware backup");
        Check(DebugStartButton.Visibility == Visibility.Visible && CanEditBreakpoints, "PICkit3 / MDB debug and source breakpoints enabled for the exact supported target");
        await ShowProjectDetailsAsync();
        Check(BuildSettingsCard.Visibility == Visibility.Collapsed, "PIC does not expose GCC/CMake build settings");
        Check(sourceRegistrationMenu?.Command?.CanExecute(null) == true, "PIC exposes CMake source registration");
        var guide = StudioX.Application.Onboarding.FirstProjectGuideService.Steps(GuideProgress());
        var scope = ProjectCapabilities.For(currentProjectManifest);
        var health = await services.ProjectHealth.InspectAsync(project);
        Check(guide[4].Action == "build" && guide[3].Instructions.Contains(scope.SourceWorkflow) &&
            guide[^1].Instructions.Contains(scope.RuntimeWorkflow) && health.Checks.Any(item => item.Detail == scope.Scope), "PIC guide, actual menus and health share one capability definition");
        var result = await BuildWithSummaryAsync(project, CancellationToken.None);
        Check(result.Success && result.Artifacts.Any(path => path.EndsWith(".hex", StringComparison.Ordinal)), "WPF build flow executes actual XC8 and presents HEX");
        Check(result.Log.Contains("2000h words", StringComparison.Ordinal), "PIC build output retains the proper program-word units");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Save(theme.Id + "-pic-workbench.png");
            var settings = new StudioX.Engine.Pic.PicDownloadSettings(ProgrammerSerial: "PREVIEW-PICKIT3-A");
            var selection = new PicDownloadWindow(settings, [new("PREVIEW-PICKIT3-A"), new("PREVIEW-PICKIT3-B")], () => throw new InvalidOperationException("Preview must not enumerate hardware")) { Owner = this };
            selection.Show();
            await SaveDialogAsync(selection, theme.Id + "-pic-settings.png");
        }
        checks.Add("PICkit3 selection renders in both themes without connecting hardware; downloads have no confirmation dialog");
        Check((await services.Builds.BuildPicDebugAsync(project)).Success, "GPIO template has valid explicit debug configuration");
        try
        {
            await services.Debugger.StartPicAsync(services.PicDebug, null);
            RefreshDebugUi();
            await NavigateSelectedDebugFrameAsync();
            Check(DebugContinueButton.IsEnabled && DebugIntoButton.IsEnabled && DebugOverButton.IsEnabled && DebugStopButton.IsEnabled,
                "PIC source debug actions are available in the real workbench");
            DebugTools.ShowDisassembly();
            DebugTools.ReadDisassemblyAt("0x0");
            await Task.Delay(500);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(DebugTools.DisassemblyInstructionCount > 0 && DebugTools.CanReadDisassembly, "PIC program-word disassembly reaches the actual UI");
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                await Save(theme.Id + "-pic-debug.png");
            }
        }
        finally { await services.Debugger.StopAsync(); }
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks
        });

        async Task SaveDialogAsync(Window window, string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, name)))
            {
                png.Save(file);
            }
            window.Close();
        }
    }
}

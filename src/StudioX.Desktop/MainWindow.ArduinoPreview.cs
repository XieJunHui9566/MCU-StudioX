namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>真实 WPF 检查仅打开隔离 sketch、预检固件及渲染窗口，不启动任何上传进程。</summary>
    public async Task RenderArduinoPreviewAsync(string directory, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string description)
        {
            if (!value)
            {
                throw new InvalidOperationException(description);
            }
            checks.Add(description);
        }
        void Save(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            encoder.Save(file);
        }
        await OpenProjectAsync(project, CancellationToken.None);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(IsArduinoProject && activeDocument?.RelativePath.EndsWith(".ino", StringComparison.Ordinal) == true, "Arduino sketch opens in the real editor");
        Check(CodeLanguage.ForFile("Sketch.ino") == "C++", "ino uses C++ syntax highlighting");
        Check(BuildButton.IsEnabled && DownloadButton.IsEnabled, "native build and upload actions enabled");
        Check(DownloadProbePicker.Visibility == Visibility.Collapsed && DebugStartButton.Visibility == Visibility.Collapsed, "OpenOCD probe and unsupported source debugging hidden");
        Check(services.Intelligence.IsReady, "real clangd starts for Arduino");
        await ShowProjectDetailsAsync();
        Check(BuildSettingsCard.Visibility == Visibility.Collapsed, "CMake-only settings hidden for Arduino");
        Check(sourceRegistrationMenu?.Command?.CanExecute(null) == false, "Arduino source registration menu does not offer a CMake operation");
        var scope = StudioX.Engine.ProjectCapabilities.For(currentProjectManifest);
        var guide = StudioX.Application.Onboarding.FirstProjectGuideService.Steps(GuideProgress());
        var health = await services.ProjectHealth.InspectAsync(project);
        Check(guide[4].Action == "build" && guide[3].Instructions.Contains(scope.SourceWorkflow) &&
            guide[^1].Instructions.Contains(scope.RuntimeWorkflow) && health.Checks.Any(item => item.Detail == scope.Scope), "Arduino guide, actual menus and health share supported and unverified boundaries");
        var manifest = await ProjectService.ReadAsync(project);
        var preview = await services.Arduino.PreviewUploadAsync(project);
        static T Find<T>(DependencyObject element) where T : DependencyObject
        {
            if (element is T found)
            {
                return found;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            {
                try
                {
                    return Find<T>(child);
                }
                catch (InvalidOperationException) { }
            }
            throw new InvalidOperationException(typeof(T).Name + " missing");
        }
        var selection = new ArduinoUploadWindow(preview, ["COM3", "COM18"]);
        Check(selection.Port == "" && Find<ComboBox>(selection).SelectedIndex == -1, "upload never guesses the first enumerated port");
        var discovered = new TaskCompletionSource<string[]>();
        var remembered = new ArduinoUploadWindow(preview, ["COM3", "COM18"], "com18", () => discovered.Task);
        Check(remembered.Port == "COM18", "upload restores the explicitly saved project port");
        var refreshing = remembered.RefreshPortsAsync();
        Find<ComboBox>(remembered).Text = "COM24";
        discovered.SetResult(["COM3"]);
        await refreshing;
        Check(remembered.Port == "COM24" && Find<ComboBox>(remembered).SelectedIndex == -1,
            "refresh preserves edits made while enumerating and never substitutes a disappearing port");
        var noVerification = new StudioX.Engine.Arduino.ArduinoUploadReport(new(0, "New upload port: COM18 (serial)", "", false, false), "fixture", false);
        Check(!noVerification.Success, "New upload port alone is not presented as verified firmware");
        // 未显示的测试窗口也属于 Application.Windows；关闭后才能随主窗口退出验证进程。
        selection.Close();
        remembered.Close();
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(this, theme.Id + "-arduino-editor.png");
            foreach (var dialog in new Window[]
            {
                new ArduinoProjectWindow(services.Arduino.Boards),
                new ArduinoManagerWindow(services.Arduino, project, manifest),
                new ArduinoUploadWindow(preview, ["COM3", "COM18"], "COM18")
            })
            {
                dialog.Owner = this;
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -20000;
                dialog.ShowActivated = false;
                dialog.ShowInTaskbar = false;
                dialog.Show();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check(dialog.ActualWidth > 0 && dialog.ActualHeight > 0, theme.Id + " " + dialog.GetType().Name + " renders");
                Save(dialog, theme.Id + "-" + dialog.GetType().Name + ".png");
                dialog.Close();
            }
        }
        await JsonStore.WriteAsync(Path.Combine(directory, "arduino-ui.json"), new
        {
            passed = true,
            hardware = false,
            checks
        });
    }
}

namespace StudioX.Desktop;

using System.Windows;
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
        var manifest = await ProjectService.ReadAsync(project);
        var preview = await services.Arduino.PreviewUploadAsync(project);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(this, theme.Id + "-arduino-editor.png");
            foreach (var dialog in new Window[]
            {
                new ArduinoProjectWindow(services.Arduino.Boards),
                new ArduinoManagerWindow(services.Arduino, project, manifest),
                new ArduinoUploadWindow(preview)
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
            hardware = "not_requested",
            checks
        });
    }
}

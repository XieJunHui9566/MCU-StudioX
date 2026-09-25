namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudioX.Application;

public partial class App : System.Windows.Application
{
    // 安装器据此要求先关闭 IDE；不强杀进程，保留用户保存未完成修改的机会。
    private Mutex? installationMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowMouseWheel.Install();
        installationMutex = new Mutex(false, "MCUStudioX.Desktop.InstallLock");
        var smoke = e.Args is ["--smoke", _];
        var showDebugDemo = e.Args is ["--show-debug-demo", _] or ["--show-debug-demo", _, _];
        var showBreakpointsDemo = e.Args is ["--show-breakpoints-demo", _] or ["--show-breakpoints-demo", _, _];
        var preview = e.Args is ["--preview-ui", _];
        var windowLayoutPreview = e.Args is ["--preview-window-layout", _];
        var editorPreview = e.Args is ["--preview-editor", _, _];
        var completionPreview = e.Args is ["--preview-completion", _, _];
        var cmakePreview = e.Args is ["--preview-cmake", _, _];
        var documentsPreview = e.Args is ["--preview-documents", _, _];
        var navigationPreview = e.Args is ["--preview-navigation", _, _];
        var explorerPreview = e.Args is ["--preview-explorer", _, _];
        var buildPreview = e.Args is ["--preview-build", _, _];
        var buildMemoryPreview = e.Args is ["--preview-memory", _, _];
        var editingPreview = e.Args is ["--preview-editing", _, _];
        var bracketsPreview = e.Args is ["--preview-brackets", _, _];
        var projectPreview = e.Args is ["--preview-project", _, _];
        var cubeMxPreview = e.Args is ["--preview-cubemx", _, _];
        var downloadPreview = e.Args is ["--preview-download", _, _];
        var debugPreview = e.Args is ["--preview-debug", _, _];
        var breakpointsPreview = e.Args is ["--preview-breakpoints", _, _];
        var importPerformancePreview = e.Args is ["--preview-import-performance", _, _];
        var stm32Preview = e.Args is ["--preview-stm32", _, _] or ["--preview-stm32", _, _, _];
        var rp2350Preview = e.Args is ["--preview-rp2350", _, _];
        var anyPreview = preview || windowLayoutPreview || editorPreview || completionPreview || cmakePreview || documentsPreview || navigationPreview || explorerPreview || buildPreview || buildMemoryPreview || editingPreview || bracketsPreview || stm32Preview || rp2350Preview || projectPreview || cubeMxPreview || importPerformancePreview || downloadPreview || debugPreview || breakpointsPreview;
        var data = (showDebugDemo || showBreakpointsDemo) && e.Args.Length == 3 ? Path.GetFullPath(e.Args[2]) : smoke || anyPreview ? Path.Combine(Path.GetFullPath(e.Args[1]), "user-data") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX");
        var services = new WorkbenchService(Path.Combine(AppContext.BaseDirectory, "runtime"), data);
        var window = new MainWindow(services);
        MainWindow = window;
        if (smoke || anyPreview) { window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; }
        window.Show();
        await window.InitializeAsync();
        if (anyPreview)
        {
            var directory = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(directory);
            try
            {
                if (windowLayoutPreview) await window.RenderWindowLayoutPreviewAsync(directory);
                else if (buildMemoryPreview) await window.RenderBuildMemoryPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (editingPreview) await window.RenderEditingPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (bracketsPreview) await window.RenderBracketsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (debugPreview) await window.RenderDebugPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (breakpointsPreview) await window.RenderBreakpointsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (downloadPreview) await window.RenderDownloadPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (cubeMxPreview) await window.RenderCubeMxPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (importPerformancePreview) await window.MeasureImportPerformanceAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (projectPreview) await window.RenderProjectPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (stm32Preview) await window.RenderStm32PreviewAsync(directory, Path.GetFullPath(e.Args[2]), e.Args.Length == 4 ? Path.GetFullPath(e.Args[3]) : null);
                else if (rp2350Preview) await window.RenderRp2350PreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (buildPreview) await window.RenderBuildPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (explorerPreview) await window.RenderExplorerPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (navigationPreview) await window.RenderNavigationPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (documentsPreview) await window.RenderDocumentsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (cmakePreview) await window.RenderCMakePreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (completionPreview) await window.RenderCompletionPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else if (editorPreview) await window.RenderEditorPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                else await window.RenderPreviewAsync(directory);
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }
            return;
        }
        if (!smoke)
        {
            if (showDebugDemo || showBreakpointsDemo)
            {
                if (showBreakpointsDemo) await window.ShowSpecialBreakpointDemoAsync(e.Args[1]);
                else await window.ShowDebugDemoAsync(e.Args[1]);
                if (services.Debugger.State == StudioX.Engine.Debugging.DebugState.Stopped)
                    await StudioX.Foundation.JsonStore.WriteAsync(Path.Combine(data, "debug-demo-ready.json"), new { processId = Environment.ProcessId, project = services.Debugger.ProjectDirectory, state = "Stopped", mode = "offline" });
                return;
            }
            if (e.Args is ["--new-project"]) await window.ShowNewProjectAsync();
            else if (e.Args is ["--import-cubemx"]) await window.ShowCubeMxImportAsync();
            else if (e.Args is ["--import-cubemx", var cubeDirectory]) await window.ShowCubeMxImportAsync(cubeDirectory);
            else if (e.Args is ["--new-project", var packId, var deviceId]) await window.ShowNewProjectAsync(packId, deviceId);
            else if (e.Args is ["--open", var project]) await window.OpenFromCommandLineAsync(project);
            else if (e.Args is ["--open", var fileProject, "--file", var relativePath]) await window.OpenFromCommandLineAsync(fileProject, relativePath);
            else if (e.Args.Length > 3 && e.Args[0] == "--open" && e.Args[2] == "--files") await window.OpenFromCommandLineAsync(e.Args[1], e.Args[3..]);
            return;
        }
        try
        {
            var output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            await window.ExerciseSimulationAsync();
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                window.ApplyTheme(theme);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, theme.Id + ".png")); encoder.Save(file);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "Window initialized; two subscribers received simulation frames; dark/light rendered.\n");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(e.Args[1]), "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { window.Close(); }
    }
}

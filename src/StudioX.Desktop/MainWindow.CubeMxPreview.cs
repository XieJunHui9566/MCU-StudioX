namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>仅传入导入验证工具生成的隔离工程；不接触原项目和硬件。</summary>
    public async Task RenderCubeMxPreviewAsync(string directory, string fixture)
    {
        var inspection = await services.CubeMx.InspectAsync(fixture);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var dialog = new CubeMxImportWindow(inspection) { Owner = this, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000 };
            dialog.Show(); dialog.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (dialog.ConfigurePreset != "Debug") throw new InvalidOperationException("Import did not select the actual Debug preset.");
            Render(dialog, Path.Combine(directory, "import-" + theme.Id + ".png")); dialog.Close();
        }
        ApplyTheme(ThemeService.Dark);
        ShowDocument(WelcomeTab); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "welcome.png"));
        await OpenFromCommandLineAsync(fixture);
        if (activeDocument?.RelativePath != "Core/Src/main.c" || !services.Intelligence.IsReady || !BuildButton.IsEnabled || !DownloadButton.IsEnabled || FindProjectNode("Core/Src")?.IsExpanded != true)
            throw new InvalidOperationException("Imported project did not open correctly: " + Status.Text);
        await OpenSourceAsync("CMakeLists.txt", CancellationToken.None);
        if (SourceEditor.IsReadOnly) throw new InvalidOperationException("User CMake became read-only.");
        var cmake = SourceEditor.Text + "\ntarget_sources(STM";
        var hints = await services.CMake.GetAsync(fixture, "CMakeLists.txt", cmake, cmake.Length);
        if (!hints.Suggestions.Any(suggestion => suggestion.Label == inspection.Name)) throw new InvalidOperationException("CubeMX CMake target completion missing.");
        await OpenSourceAsync("Core/Src/main.c", CancellationToken.None);
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        if (Status.Text != "编译成功，退出代码：0" || !BuildLog.Text.TrimEnd().EndsWith(Status.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Imported desktop build failed: " + BuildLog.Text);
        var position = SourceEditor.Text.IndexOf("HAL_Init();", StringComparison.Ordinal) + 3;
        QueueCodeNavigation(true, position); await navigationTask;
        if (activeDocument?.RelativePath.EndsWith("hal.h", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("Imported editor declaration jump failed: " + Status.Text);
        await TravelNavigationAsync(backwards: true);
        SourceEditor.ScrollToLine(65); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "workspace.png"));
        await CloseProjectAsync(CancellationToken.None);
        if (ProjectTree.Items.Count != 0 || services.Intelligence.IsReady) throw new InvalidOperationException("Import workspace did not close.");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: import dialog in both themes, welcome entry, imported Core/Src tree and main tab, CMake editing/target completion, desktop build summary, declaration jump/back, close cleanup.\n");
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderBuildAssemblyPreviewAsync(string output, string project)
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
        await OpenProjectAsync(project, default);
        var catalog = await services.BuildAssembly.ListAsync(project);
        var sources = catalog.Files.Select(file => file.SourcePath).OfType<string>().Distinct().ToArray();
        var entry = sources.FirstOrDefault(file => Path.GetFileName(file) == "main.c") ?? sources.First();
        await OpenSourceAsync(entry, default);
        await RefreshVisibleBuildAssemblyAsync(project, default);
        Check(buildAssembly.Visibility == Visibility.Collapsed && !BuildAssemblyMenu.IsChecked,
            "completed build leaves the closed assembly pane closed");
        Check(BuildAssemblyButton.IsEnabled && BuildAssemblyButton.ToolTip is string hint && hint.Contains("编译", StringComparison.Ordinal) &&
            System.Windows.Controls.ToolTipService.GetShowOnDisabled(BuildAssemblyButton),
            "source toolbar exposes manual assembly access and its compilation prerequisite");
        BuildAssemblyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await buildAssemblyLoadTask;
        Check(buildAssembly.FileCount > 0 && buildAssembly.Editor.Text.Length > 0 && buildAssembly.Visibility == Visibility.Visible,
            "real build assembly opens beside the source editor");
        Check(buildAssembly.Editor.IsReadOnly && BuildAssemblyMenu.IsEnabled && BuildAssemblyMenu.IsChecked,
            "assembly view is read-only and reachable through the build menu");
        Check(buildAssembly.StatusText.Contains("地址 / 机器码", StringComparison.Ordinal), "linked addresses and machine bytes are clearly labelled");
        if (sources.FirstOrDefault(file => file != entry) is { } other)
        {
            await OpenSourceAsync(other, default);
            await buildAssemblyLoadTask;
            Check(buildAssembly.SelectedFile?.SourcePath == other, "switching the source tab selects its assembly unit");
            await OpenSourceAsync(entry, default);
            await buildAssemblyLoadTask;
        }
        var originalFont = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "studiox.assembly-audit",
            DisplayName = "Assembly audit",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        try
        {
            foreach (var (name, theme) in new[] { ("dark", ThemeService.Dark), ("light", ThemeService.Light), ("custom", custom) })
            {
                ApplyTheme(theme);
                foreach (var size in new[] { 13d, 18d })
                {
                    System.Windows.Application.Current.Resources["WorkbenchFontSize"] = size;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await Task.Delay(50);
                    Check(buildAssembly.ActualWidth > 150 && buildAssembly.Editor.ActualHeight > 200 && buildAssembly.Editor.IsReadOnly,
                        name + size + " assembly layout remains readable and read-only");
                    var outlineRight = OutlineToggle.TranslatePoint(new Point(OutlineToggle.ActualWidth, 0), EditorSurface).X;
                    var assemblyLeft = BuildAssemblyButton.TranslatePoint(new Point(), EditorSurface).X;
                    Check(assemblyLeft >= outlineRight && BuildAssemblyButton.ActualHeight >= BuildAssemblyButton.FontSize + 4,
                        name + size + " assembly button stays to the right of file structure and fits the toolbar");
                    UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(this);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, name + "-" + size + ".png"));
                    encoder.Save(file);
                }
            }
            var original = activeEditor!.Buffer.Text;
            activeEditor.Buffer.Text += "\n/* unsaved edit */";
            Check(buildAssembly.Editor.Text.Length == 0, "unsaved source edit immediately clears old assembly");
            activeEditor.Buffer.Text = original;
            await RefreshBuildAssemblyAsync(projectDirectory, true, default);
            Check(buildAssembly.Editor.Text.Length > 0, "refresh restores valid assembly after the unchanged source is restored");
            ShowBuildAssembly(false);
            Check(buildAssembly.Visibility == Visibility.Collapsed, "assembly pane can close without closing source");
            await RefreshVisibleBuildAssemblyAsync(project, default);
            Check(buildAssembly.Visibility == Visibility.Collapsed, "a subsequent build refresh does not reopen the closed assembly pane");
            BuildAssemblyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await buildAssemblyLoadTask;
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalFont;
            ApplyTheme(ThemeService.Dark);
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = false,
            screenshots = 6,
            checks
        });
    }
}

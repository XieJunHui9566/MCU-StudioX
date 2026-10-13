namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderStcTimingPreviewAsync(string output, string project)
    {
        var checks = new List<string>();
        void Check(bool condition, string title)
        {
            if (!condition)
            {
                throw new InvalidOperationException(title);
            }
            checks.Add(title);
        }
        projectDirectory = project;
        currentProjectManifest = await ProjectService.ReadAsync(project);
        var context = await services.StcTiming.ReadAsync(project);
        const string original = "void main(void)\r\n{\r\n    for (;;) {}\r\n}\r\n";
        ShowSource(new("timing-ui.c", original, Encoding.UTF8, "fixture", false));
        var target = CaptureTemplateTarget(start: 0, length: 0)!;
        var settings = context.Settings with
        {
            TimerDivider = 1,
            BaudRate = 115200,
            BaudGenerator = "timer2",
            UartPins = "p36"
        };
        var generated = services.StcTiming.GenerateUart(context, settings, "sx_ui_uart");
        await ApplyStcTimingAsync(target, context, generated);
        Check(activeEditor!.IsDirty && SourceEditor.Text.Contains("sx_ui_uart", StringComparison.Ordinal), "generated code enters only the unsaved C buffer");
        Check(!File.Exists(Path.Combine(project, "timing-ui.c")), "insertion does not create or overwrite source on disk");
        SourceEditor.Undo();
        Check(SourceEditor.Text == original, "one undo removes whole generated function");
        SourceEditor.Redo();
        Check(SourceEditor.Text.Contains("sx_ui_uart", StringComparison.Ordinal), "redo restores generated function");
        SourceEditor.Undo();
        var stale = CaptureTemplateTarget(start: 0, length: 0)!;
        SourceEditor.AppendText("// subsequent edit\r\n");
        try
        {
            await ApplyStcTimingAsync(stale, context, generated);
            throw new InvalidOperationException("stale target accepted");
        }
        catch (StudioXException ex) when (ex.Code == "STC_TIMING_STALE")
        {
            Check(SourceEditor.Text.EndsWith("// subsequent edit\r\n", StringComparison.Ordinal), "stale insertion preserves subsequent edits");
        }
        SourceEditor.Undo();
        var readOnly = CaptureTemplateTarget(start: 0, length: 0)!;
        SourceEditor.IsReadOnly = true;
        try
        {
            await ApplyStcTimingAsync(readOnly, context, generated);
            throw new InvalidOperationException("read-only target accepted");
        }
        catch (StudioXException ex) when (ex.Code == "STC_TIMING_STALE")
        {
            Check(SourceEditor.Text == original, "read-only target is not modified");
        }
        SourceEditor.IsReadOnly = false;
        Check(ProjectToolsMenu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "51 延时与波特率…"), "project tools menu contains generator entry");
        Check(workbenchCommands.Any(c => c.Title == "51 延时与波特率…"), "command palette contains generator entry");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var window = new StcTimingWindow(services.StcTiming, context, true, Log)
            {
                Owner = this,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000
            };
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Check(window.ClockField.IsReadOnly && window.ClockField.Text == "12000000", theme.Id + " saved project clock is used and read-only");
            Check(window.PreviewResult is not null && window.PreviewField.ActualWidth > 450 && window.InsertButton.IsEnabled, theme.Id + " delay preview is readable and insertable");
            Render(window, Path.Combine(output, "stc-delay-" + theme.Id + ".png"));
            window.KindPicker.SelectedIndex = 1;
            Check(!window.InsertButton.IsEnabled && window.PreviewResult?.CanInsert == false, theme.Id + " Mon51 default pins visibly block insertion");
            window.PinsPicker.SelectedItem = "p36";
            window.DividerPicker.SelectedItem = 1;
            window.BaudField.Text = "115200";
            Check(window.InsertButton.IsEnabled && window.PreviewField.Text.Contains("T2L = 0xE6;", StringComparison.Ordinal) && window.PreviewResult?.Reload == 0xFFE6, theme.Id + " alternate UART1 pins and accurate Timer2 reload are accepted");
            window.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(window, Path.Combine(output, "stc-uart-" + theme.Id + ".png"));
            window.GeneratorPicker.SelectedItem = "timer1";
            Check(!window.InsertButton.IsEnabled && window.SummaryField.Text.Contains("误差", StringComparison.Ordinal), theme.Id + " excessive baud error keeps calculated result visible");
            window.BaudField.Text = "invalid";
            Check(window.PreviewResult is null && !window.InsertButton.IsEnabled, theme.Id + " invalid numeric input prevents generation");
            window.Close();
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks.Select(c => "PASS " + c).Append($"PASS {checks.Count} checks"));
    }
}

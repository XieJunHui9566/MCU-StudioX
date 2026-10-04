namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    public async Task RenderDiagnosticsPreviewAsync(string directory)
    {
        diagnosticPoll.Stop();
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        EditorProblemRow[] rows =
        [
            new(new("src/main.c", 12, 5, false, "Use of undeclared identifier 'LED1'"), "实时 · clangd", ""),
            new(new("device/studiox/StudioX_System.h", 6, 1, true, "Included header alta.h is not used directly (fix available)"), "实时 · clangd", ""),
            new(new("src/main.c", 18, 9, true, "unused variable ‘counter’ [-Wunused-variable]"), "构建", ""),
            new(new("include/board.h", 3, 1, false, "'device.h' file not found\nIncluded from board.h:3"), "构建", "")
        ];
        ProblemsGrid.ItemsSource = rows;
        ProblemsGrid.SelectedItem = rows[1];
        ProblemsTab.Header = $"问题 ({rows.Length})";
        ShowBottom(4);
        BottomRow.Height = new GridLength(330);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var labels = Descendants<TextBlock>(ProblemsGrid).ToArray();
            var row = (DataGridRow)ProblemsGrid.ItemContainerGenerator.ContainerFromItem(rows[1]);
            Check(labels.Any(t => t.Text == rows[1].ChineseMessage) && labels.Any(t => t.Text == rows[1].Message), theme.Id + ": Chinese and original English both rendered");
            Check(Descendants<TextBlock>(row).Single(t => t.Text == "警告 Warning").Foreground.ToString() == FindResource("DiagnosticWarning").ToString(), theme.Id + ": selected warning keeps severity color");
            Check(labels.First(t => t.Text == "错误 Error").Foreground.ToString() == FindResource("DiagnosticError").ToString(), theme.Id + ": error is colored independently of warning");
            Check(row.ActualHeight >= 48 && !double.IsNaN(row.ActualHeight), theme.Id + ": bilingual row expands vertically");
            Render(ProblemsGrid, Path.Combine(directory, "problems-" + theme.Id + ".png"));
        }
        Width = 1100;
        Height = 760;
        ApplyTheme(ThemeService.Dark);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(ProblemsGrid.Columns[^1].ActualWidth >= 240, "compact window keeps a readable description column");
        var column = ProblemsGrid.Columns[^1];
        Check(column.ClipboardContentBinding is System.Windows.Data.Binding { Path.Path: "BilingualMessage" }, "copying diagnostic description includes both languages");
        Render(this, Path.Combine(directory, "problems-compact.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));

        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    yield return match;
                }
                foreach (var descendant in Descendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

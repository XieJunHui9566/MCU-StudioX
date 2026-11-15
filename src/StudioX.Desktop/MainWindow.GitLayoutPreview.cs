namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>只读取隔离仓库，用真实列表选择检查图谱布局和详情切换。</summary>
    public async Task RenderGitLayoutPreviewAsync(string directory, string emptyRepository, string historyRepository)
    {
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }
        async Task WaitForDiff()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Settle();
                if (GitGraph.DiffBox.Text.Contains("diff --git", StringComparison.Ordinal))
                {
                    return;
                }
                await Task.Delay(50);
            }
            throw new InvalidOperationException("Git detail did not load: " + GitGraph.GitStatus.Text);
        }
        void CheckBounds(FrameworkElement element, string label)
        {
            var bounds = element.TransformToAncestor(GitGraph).TransformBounds(new Rect(element.RenderSize));
            Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= GitGraph.ActualWidth + 1 &&
                bounds.Bottom <= GitGraph.ActualHeight + 1 && bounds.Width > 0 && bounds.Height > 0, label);
        }
        var originalFont = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "studiox.git-layout-audit",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        try
        {
            ShowDocument(GitGraphTab);
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                Width = theme == custom ? 1120 : 1640;
                Height = theme == custom ? 860 : 1000;
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                GitGraph.SetProject(emptyRepository);
                await GitGraph.EnsureLoadedAsync();
                await Settle();
                Check(GitGraph.EmptyHistory.IsVisible && GitGraph.EmptyHistoryTitle.Text == "尚无提交" &&
                    !GitGraph.DetailPane.IsVisible && GitGraph.DetailRow.ActualHeight == 0,
                    theme.Id + " empty repository has an inline hint and no blank detail panel");
                CheckBounds(GitGraph.RepositoryActions, theme.Id + " repository toolbar fits");
                CheckBounds(GitGraph.SearchBox, theme.Id + " search fits");
                Render(GitGraph, Path.Combine(directory, theme.Id + "-empty.png"));

                GitGraph.WorkingFilesList.SelectedIndex = 0;
                await WaitForDiff();
                Check(GitGraph.DetailPane.IsVisible && !GitGraph.ChangedFilesPane.IsVisible &&
                    GitGraph.DiffPane.ActualWidth > GitGraph.HistoryWorkspace.ActualWidth - 2,
                    theme.Id + " untracked file uses full-width diff before the first commit");
                CheckBounds(GitGraph.DiffBox, theme.Id + " working diff fits");
                Render(GitGraph, Path.Combine(directory, theme.Id + "-working.png"));

                GitGraph.SetProject(historyRepository);
                await GitGraph.EnsureLoadedAsync();
                await WaitForDiff();
                Check(GitGraph.CommitList.Items.Count >= 3 && !GitGraph.EmptyHistory.IsVisible &&
                    GitGraph.DetailPane.IsVisible && GitGraph.ChangedFilesPane.IsVisible,
                    theme.Id + " history selection shows files and diff below the graph");
                var historyBottom = GitGraph.HistoryPane.TranslatePoint(new Point(0, GitGraph.HistoryPane.ActualHeight), GitGraph).Y;
                var detailTop = GitGraph.DetailPane.TranslatePoint(new Point(), GitGraph).Y;
                Check(detailTop >= historyBottom && GitGraph.DiffBox.ActualWidth >= 180,
                    theme.Id + " details do not overlap the history and keep a readable diff");
                CheckBounds(GitGraph.DetailPane, theme.Id + " bottom detail panel fits");
                Check(Descendants(GitGraph.CommitList).OfType<ScrollViewer>().First().ScrollableWidth == 0,
                    theme.Id + " commit rows fit the header without horizontal scrolling");
                Render(GitGraph, Path.Combine(directory, theme.Id + "-history.png"));

                GitGraph.ChangedFilesList.SelectedIndex = GitGraph.ChangedFilesList.Items.Count - 1;
                await WaitForDiff();
                var initialHeight = GitGraph.DetailPane.ActualHeight;
                GitGraph.DetailSplitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                GitGraph.DetailSplitter.RaiseEvent(new DragDeltaEventArgs(0, -20) { RoutedEvent = Thumb.DragDeltaEvent });
                GitGraph.DetailSplitter.RaiseEvent(new DragCompletedEventArgs(0, -20, false) { RoutedEvent = Thumb.DragCompletedEvent });
                await Settle();
                Check(GitGraph.DetailPane.ActualHeight > initialHeight + 10, theme.Id + " real splitter increases detail height");
                GitGraph.SearchBox.Text = "no_matching_commit_917";
                await Settle();
                Check(GitGraph.EmptyHistoryTitle.Text == "没有匹配的提交" && !GitGraph.DetailPane.IsVisible,
                    theme.Id + " empty filter hides stale details");
                GitGraph.SearchBox.Clear();
                await WaitForDiff();
                Check(GitGraph.DetailPane.ActualHeight > initialHeight + 10, theme.Id + " clearing search restores detail height");

                GitGraph.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                GitGraph.CommitList.SelectedIndex = 1;
                await WaitForDiff();
                GitGraph.CompareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForDiff();
                Check(GitGraph.DetailKind.Text == "提交比较" && !GitGraph.ChangedFilesPane.IsVisible &&
                    GitGraph.DiffPane.ActualWidth > GitGraph.HistoryWorkspace.ActualWidth - 2,
                    theme.Id + " comparing commits uses the full detail width");

                GitGraph.SetProject(null);
                await Settle();
                Check(!GitGraph.DetailPane.IsVisible && GitGraph.EmptyHistory.IsVisible,
                    theme.Id + " closing a repository clears details");
            }
            await JsonStore.WriteAsync(Path.Combine(directory, "git-layout-result.json"), new
            {
                passed = true,
                checks,
                screenshots = 9,
                network = false,
                repositoryWrites = false
            });
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalFont;
            ApplyTheme(ThemeService.Dark);
        }
    }
}

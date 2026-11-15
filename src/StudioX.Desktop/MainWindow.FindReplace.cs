namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    private bool CanFindSource => activeDocument is not null && IsActiveSourceTab;
    private bool updatingFind;
    private readonly DispatcherTimer findTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly SemaphoreSlim findGate = new(1, 1);
    private CancellationTokenSource? findCancellation;
    private TextSearchOptions SearchOptions => new(FindText.Text, FindCase.IsChecked == true, FindWord.IsChecked == true, FindRegex.IsChecked == true);

    private void InitializeFindReplace()
    {
        CommandBindings.Add(new CommandBinding(SourceCommands.Find, (_, _) => ShowFindReplace(false),
            (_, e) =>
            {
                e.CanExecute = CanFindSource;
                e.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(SourceCommands.Replace, (_, _) => ShowFindReplace(true),
            (_, e) =>
            {
                e.CanExecute = CanEditSource;
                e.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(SourceCommands.ReplaceAll, (_, _) => ShowFindReplace(true),
            (_, e) =>
            {
                e.CanExecute = CanEditSource;
                e.Handled = true;
            }));
        SourceEditor.DocumentChanged += (_, _) => RefreshFindStatus();
        SourceEditor.TextChanged += (_, _) => RefreshFindStatus();
        findTimer.Tick += async (_, _) => await UpdateFindStatusAsync();
    }

    private void ShowFindReplace(bool replace)
    {
        if (!CanFindSource)
        {
            return;
        }
        CloseCodeAssistance();
        FindPanel.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        if (SourceEditor.SelectionLength is > 0 and < 512 && !SourceEditor.SelectedText.Contains('\n'))
        {
            FindText.Text = SourceEditor.SelectedText;
        }
        RefreshFindStatus();
        FindText.Focus();
        FindText.SelectAll();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = Visibility.Collapsed;
        CancelFind();
        SourceEditor.Focus();
    }

    private void Search_Changed(object sender, RoutedEventArgs e) => RefreshFindStatus();

    private void RefreshFindStatus()
    {
        if (updatingFind)
        {
            return;
        }
        CancelFind();
        if (FindPanel is null || FindText is null || FindStatus is null || FindCase is null || FindWord is null || FindRegex is null ||
            FindPanel.Visibility != Visibility.Visible || closing)
        {
            return;
        }
        ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = false;
        FindStatus.Text = FindText.Text.Length == 0 ? "当前文件 · 输入查找内容" : "当前文件 · 正在查找…";
        if (FindText.Text.Length > 0)
        {
            findTimer.Start();
        }
    }

    private void CancelFind()
    {
        findTimer.Stop();
        findCancellation?.Cancel();
    }

    private Task UpdateFindStatusAsync() => SearchDocumentAsync(null, "查找无效：", matches =>
    {
        FindStatus.Text = FindText.Text.Length == 0 ? "当前文件 · 输入查找内容" : $"当前文件 · {matches.Count} 处匹配";
        ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = CanEditSource && matches.Count > 0;
    });

    private async Task SearchDocumentAsync(string? replacement, string errorPrefix, Action<IReadOnlyList<TextSearchMatch>> apply)
    {
        CancelFind();
        if (!CanFindSource || closing || FindPanel.Visibility != Visibility.Visible)
        {
            return;
        }
        var document = SourceEditor.Document;
        var version = document.Version;
        var options = SearchOptions;
        var text = document.Text;
        var request = new CancellationTokenSource();
        findCancellation = request;
        var applying = false;
        bool Current() => !closing && !request.IsCancellationRequested && findCancellation == request &&
            SourceEditor.Document == document && document.Version == version && SearchOptions == options &&
            FindPanel.Visibility == Visibility.Visible && (replacement is null || ReplaceText.Text == replacement);
        try
        {
            // 连续输入合并到最新请求；旧正则退出前不再启动另一个匹配工作线程。
            await findGate.WaitAsync(request.Token);
            IReadOnlyList<TextSearchMatch> matches;
            try
            {
                matches = await Task.Run(() => TextSearchService.Find(text, options, replacement, request.Token), request.Token);
            }
            finally
            {
                findGate.Release();
            }
            if (Current())
            {
                applying = true;
                apply(matches);
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (applying || Current())
            {
                FindStatus.Text = errorPrefix + ex.Message;
                ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = false;
            }
        }
        finally
        {
            if (findCancellation == request)
            {
                findCancellation = null;
            }
            request.Dispose();
        }
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindMatchAsync(false);
    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindMatchAsync(true);

    private Task FindMatchAsync(bool backwards) => SearchDocumentAsync(null, "查找无效：", matches => SelectFindMatch(matches, backwards));

    private void SelectFindMatch(IReadOnlyList<TextSearchMatch> matches, bool backwards)
    {
        if (matches.Count == 0)
        {
            FindStatus.Text = "当前文件 · 0 处匹配";
            return;
        }
        var position = SourceEditor.SelectionStart;
        var current = -1;
        var index = -1;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.Start == position && match.Length == SourceEditor.SelectionLength)
            {
                current = i;
            }
            if (backwards ? match.Start < position : index < 0 && match.Start >= position)
            {
                index = i;
            }
        }
        if (current >= 0)
        {
            index = (current + (backwards ? matches.Count - 1 : 1)) % matches.Count;
        }
        if (index < 0)
        {
            index = backwards ? matches.Count - 1 : 0;
        }
        SelectMatch(matches[index]);
        FindStatus.Text = $"当前文件 · 第 {index + 1} / {matches.Count} 处";
        ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = CanEditSource;
    }

    private void SelectMatch(TextSearchMatch match)
    {
        SourceEditor.Select(match.Start, match.Length);
        var location = SourceEditor.Document.GetLocation(match.Start);
        SourceEditor.ScrollTo(location.Line, location.Column);
    }

    private async void ReplaceOne_Click(object sender, RoutedEventArgs e) => await ReplaceMatchesAsync(false);
    private async void ReplaceAll_Click(object sender, RoutedEventArgs e) => await ReplaceMatchesAsync(true);

    private async Task ReplaceMatchesAsync(bool all)
    {
        if (!CanEditSource)
        {
            return;
        }
        var selectionStart = SourceEditor.SelectionStart;
        var selectionLength = SourceEditor.SelectionLength;
        var advance = false;
        await SearchDocumentAsync(ReplaceText.Text, "替换失败：", matches =>
        {
            if (!CanEditSource || !all && (SourceEditor.SelectionStart != selectionStart || SourceEditor.SelectionLength != selectionLength))
            {
                return;
            }
            var document = SourceEditor.Document;
            if (!all)
            {
                var match = matches.FirstOrDefault(item => item.Start == SourceEditor.SelectionStart && item.Length == SourceEditor.SelectionLength);
                if (match is null)
                {
                    SelectFindMatch(matches, false);
                    return;
                }
                matches = [match];
            }
            // 先校验完整替换计划，正则错误或超限时不产生部分写入；一次操作对应一次撤销。
            updatingFind = true;
            try
            {
                using (document.RunUpdate())
                {
                    foreach (var match in matches.Reverse())
                    {
                        document.Replace(match.Start, match.Length, match.Replacement);
                    }
                }
            }
            finally
            {
                updatingFind = false;
            }
            FindStatus.Text = $"已替换 {matches.Count} 处 · 当前文件 · Ctrl+Z 可撤销";
            if (!all && matches.Count > 0)
            {
                var next = matches[0].Start + matches[0].Replacement.Length;
                SourceEditor.Select(Math.Min(next, document.TextLength), 0);
                advance = true;
            }
        });
        if (advance)
        {
            await FindMatchAsync(false);
        }
    }

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFind_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await FindMatchAsync(Keyboard.Modifiers == ModifierKeys.Shift);
        }
    }
}

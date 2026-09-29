namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using StudioX.Application;

public partial class MainWindow
{
    private bool CanFindSource => activeDocument is not null && IsActiveSourceTab;
    private bool updatingFind;
    private TextSearchOptions SearchOptions => new(FindText.Text, FindCase.IsChecked == true, FindWord.IsChecked == true, FindRegex.IsChecked == true);

    private void InitializeFindReplace()
    {
        CommandBindings.Add(new CommandBinding(SourceCommands.Find, (_, _) => ShowFindReplace(false),
            (_, e) => { e.CanExecute = CanFindSource; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(SourceCommands.Replace, (_, _) => ShowFindReplace(true),
            (_, e) => { e.CanExecute = CanEditSource; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(SourceCommands.ReplaceAll, (_, _) => ShowFindReplace(true),
            (_, e) => { e.CanExecute = CanEditSource; e.Handled = true; }));
        SourceEditor.DocumentChanged += (_, _) => RefreshFindStatus();
        SourceEditor.TextChanged += (_, _) => RefreshFindStatus();
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
        SourceEditor.Focus();
    }

    private void Search_Changed(object sender, RoutedEventArgs e) => RefreshFindStatus();

    private void RefreshFindStatus()
    {
        if (FindPanel is null || FindText is null || FindStatus is null || FindCase is null || FindWord is null || FindRegex is null ||
            FindPanel.Visibility != Visibility.Visible || updatingFind)
        {
            return;
        }
        try
        {
            var matches = TextSearchService.Find(SourceEditor.Text, SearchOptions);
            FindStatus.Text = FindText.Text.Length == 0 ? "当前文件 · 输入查找内容" : $"当前文件 · {matches.Count} 处匹配";
            ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = CanEditSource && matches.Count > 0;
        }
        catch (Exception ex)
        {
            FindStatus.Text = "查找无效：" + ex.Message;
            ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = false;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindMatch(false);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindMatch(true);

    private void FindMatch(bool backwards)
    {
        if (!CanFindSource)
        {
            return;
        }
        try
        {
            var matches = TextSearchService.Find(SourceEditor.Text, SearchOptions);
            if (matches.Count == 0)
            {
                RefreshFindStatus();
                return;
            }
            var position = SourceEditor.SelectionStart;
            var current = matches.ToList().FindIndex(item => item.Start == position && item.Length == SourceEditor.SelectionLength);
            var index = current >= 0 ? (current + (backwards ? matches.Count - 1 : 1)) % matches.Count :
                backwards ? matches.ToList().FindLastIndex(item => item.Start < position) : matches.ToList().FindIndex(item => item.Start >= position);
            if (index < 0)
            {
                index = backwards ? matches.Count - 1 : 0;
            }
            SelectMatch(matches[index]);
            FindStatus.Text = $"当前文件 · 第 {index + 1} / {matches.Count} 处";
        }
        catch (Exception ex) { FindStatus.Text = "查找无效：" + ex.Message; }
    }

    private void SelectMatch(TextSearchMatch match)
    {
        SourceEditor.Select(match.Start, match.Length);
        var location = SourceEditor.Document.GetLocation(match.Start);
        SourceEditor.ScrollTo(location.Line, location.Column);
    }

    private void ReplaceOne_Click(object sender, RoutedEventArgs e) => ReplaceMatches(false);
    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceMatches(true);

    private void ReplaceMatches(bool all)
    {
        if (!CanEditSource)
        {
            return;
        }
        try
        {
            var document = SourceEditor.Document;
            var matches = TextSearchService.Find(document.Text, SearchOptions, ReplaceText.Text);
            if (!all)
            {
                var match = matches.FirstOrDefault(item => item.Start == SourceEditor.SelectionStart && item.Length == SourceEditor.SelectionLength);
                if (match is null)
                {
                    FindMatch(false);
                    return;
                }
                matches = [match];
            }
            // 先校验完整替换计划，正则错误或超限时不产生部分写入；一次操作对应一次撤销。
            updatingFind = true;
            using (document.RunUpdate())
            {
                foreach (var match in matches.Reverse())
                {
                    document.Replace(match.Start, match.Length, match.Replacement);
                }
            }
            FindStatus.Text = $"已替换 {matches.Count} 处 · 当前文件 · Ctrl+Z 可撤销";
            if (!all && matches.Count > 0)
            {
                var next = matches[0].Start + matches[0].Replacement.Length;
                SourceEditor.Select(Math.Min(next, document.TextLength), 0);
                FindMatch(false);
            }
        }
        catch (Exception ex) { FindStatus.Text = "替换失败：" + ex.Message; }
        finally { updatingFind = false; }
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseFind_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FindMatch(Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
    }
}

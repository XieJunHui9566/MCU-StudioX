namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;

public partial class WorkspaceSearchView : UserControl
{
    private bool semanticPlan;
    private sealed record MatchRow(int Offset, int Line, int Column, string Text);
    public Action<bool>? SearchRequested
    {
        get; set;
    }
    public Action? ApplyRequested
    {
        get; set;
    }
    public Action? UndoRequested
    {
        get; set;
    }
    public Action<WorkspaceFileChange, int>? NavigateRequested
    {
        get; set;
    }
    public WorkspaceSearchView()
    {
        InitializeComponent();
    }
    public TextSearchOptions Options => new(Pattern.Text, MatchCase.IsChecked == true, WholeWord.IsChecked == true, Regex.IsChecked == true);
    public IReadOnlyList<WorkspaceFileChange> SelectedChanges => Files.Items.Cast<WorkspaceChangeRow>().Where(r => r.Selected && r.Change.CanApply).Select(r => r.Change).ToArray();
    public void BeginSearch(string? selection, bool replace)
    {
        if (semanticPlan)
        {
            SetResult([], "输入查找内容，重新生成工程搜索结果。");
        }
        SearchControls.Visibility = Visibility.Visible;
        SelectionColumn.IsReadOnly = false;
        if (!string.IsNullOrEmpty(selection))
        {
            Pattern.Text = selection;
        }
        if (replace)
        {
            Replacement.Focus();
        }
        else
        {
            Pattern.Focus();
        }
    }
    public void SetResult(IReadOnlyList<WorkspaceFileChange> changes, string message, bool rename = false)
    {
        semanticPlan = rename;
        SearchControls.Visibility = rename ? Visibility.Collapsed : Visibility.Visible;
        SelectionColumn.IsReadOnly = rename;
        Files.ItemsSource = changes.Select(c => new WorkspaceChangeRow(c)).ToArray();
        Status.Text = message;
        Apply.IsEnabled = changes.Any(c => c.CanApply);
        Files.SelectedIndex = changes.Count > 0 ? 0 : -1;
        if (changes.Count == 0)
        {
            Before.Clear();
            After.Clear();
            Matches.ItemsSource = null;
        }
    }
    public void Clear()
    {
        SetResult([], "搜索当前工程；使用编辑缓冲区。已排除构建产物、版本库与链接。");
        Undo.IsEnabled = false;
    }
    private void Find_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke(false);
    private void Preview_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke(true);
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Files.CommitEdit(DataGridEditingUnit.Cell, true);
        Files.CommitEdit(DataGridEditingUnit.Row, true);
        ApplyRequested?.Invoke();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void File_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Files.SelectedItem is not WorkspaceChangeRow row)
        {
            return;
        }
        // 预览显示有界，实际计划保留完整文本；不把显示截断当作待写内容。
        string Preview(string value) => value.Length <= 200000 ? value : value[..200000] + "\n[预览已截断；修改计划保留全文]";
        Before.Text = Preview(row.Change.Before);
        After.Text = Preview(row.Change.After);
        Matches.ItemsSource = row.Change.Matches.Select(m =>
        {
            var at = CodePositions.FromOffset(row.Change.Before, m.Start);
            var start = m.Start - at.Character;
            var end = row.Change.Before.IndexOf('\n', m.Start);
            if (end < 0)
            {
                end = row.Change.Before.Length;
            }
            return new MatchRow(m.Start, at.Line + 1, at.Character + 1, row.Change.Before[start..Math.Min(end, start + 500)].TrimEnd('\r'));
        }).ToArray();
    }
    private void Match_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Files.SelectedItem is WorkspaceChangeRow file && Matches.SelectedItem is MatchRow match)
        {
            NavigateRequested?.Invoke(file.Change, match.Offset);
        }
    }
}

namespace StudioX.Desktop;

using System.Windows;

public partial class GitGraphView
{
    private GridLength detailHeight = new(1, GridUnitType.Star);
    private GridLength changedFilesWidth = new(230);

    private void HistoryPane_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窄文档保留主题与提交号，详细作者和时间仍可在下方查看。
        var compact = e.NewSize.Width < 900;
        Resources["GitAuthorWidth"] = new GridLength(compact ? 0 : 106);
        Resources["GitDateWidth"] = new GridLength(compact ? 0 : 120);
    }

    private void ShowEmptyHistory(string title, string hint)
    {
        // 筛选为空或切换工程时，迟到的详情不能重新占据已收起的区域。
        detailVersion++;
        selectedCommitHash = null;
        selectedDetails = null;
        EmptyHistoryTitle.Text = title;
        EmptyHistoryHint.Text = hint;
        EmptyHistory.Visibility = Visibility.Visible;
        if (DetailPane.Visibility == Visibility.Visible)
        {
            detailHeight = DetailRow.Height;
        }
        DetailPane.Visibility = DetailSplitter.Visibility = Visibility.Collapsed;
        DetailRow.MinHeight = 0;
        DetailRow.Height = DetailSplitterRow.Height = new GridLength(0);
    }

    private void ShowDetailPane(string kind, bool hasFileList)
    {
        if (DetailPane.Visibility != Visibility.Visible)
        {
            DetailRow.Height = detailHeight;
            DetailRow.MinHeight = 180;
            DetailSplitterRow.Height = new GridLength(4);
            DetailPane.Visibility = DetailSplitter.Visibility = Visibility.Visible;
        }
        DetailKind.Text = kind;
        CompareButton.Visibility = hasFileList ? Visibility.Visible : Visibility.Collapsed;
        if (ChangedFilesPane.Visibility == Visibility.Visible && !hasFileList)
        {
            changedFilesWidth = ChangedFilesColumn.Width;
        }
        ChangedFilesPane.Visibility = ChangedFilesSplitter.Visibility = hasFileList ? Visibility.Visible : Visibility.Collapsed;
        ChangedFilesColumn.MinWidth = hasFileList ? 150 : 0;
        ChangedFilesColumn.Width = hasFileList ? changedFilesWidth : new GridLength(0);
        ChangedFilesSplitterColumn.Width = new GridLength(hasFileList ? 4 : 0);
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Help;

public partial class HelpCenterView : UserControl
{
    private readonly HelpContentService content;
    private bool updating;
    private bool compact;
    public HelpArticle? SelectedArticle => ArticleList.SelectedItem as HelpArticle;

    public HelpCenterView(HelpContentService content)
    {
        this.content = content;
        InitializeComponent();
        CategoryPicker.ItemsSource = new[] { "全部主题" }.Concat(content.Categories).ToArray();
        CategoryPicker.SelectedIndex = 0;
        SizeChanged += (_, _) =>
        {
            var next = ActualWidth < 900;
            if (next != compact)
            {
                compact = next;
                SetCatalogVisible(!compact);
            }
        };
        Open("quick-start");
    }

    public void Open(string id)
    {
        var article = content.Get(id);
        updating = true;
        SearchBox.Text = "";
        CategoryPicker.SelectedIndex = 0;
        updating = false;
        Refresh(article.Id);
    }

    public void Filter(string query, string? category = null)
    {
        updating = true;
        SearchBox.Text = query;
        CategoryPicker.SelectedItem = category ?? "全部主题";
        updating = false;
        Refresh();
    }

    private void Refresh(string? select = null)
    {
        if (updating || CategoryPicker is null || SearchBox is null || ArticleList is null)
        {
            return;
        }
        var results = content.Search(SearchBox.Text, CategoryPicker.SelectedIndex <= 0 ? null : CategoryPicker.SelectedItem as string);
        ArticleList.ItemsSource = results;
        ArticleList.SelectedItem = results.FirstOrDefault(article => article.Id == select) ?? results.FirstOrDefault();
        ResultStatus.Text = results.Count == 0 ? "没有匹配的主题。可清除搜索，或用“编译”“串口”“插件”等关键词。"
            : $"{results.Count} / {content.Articles.Count} 篇主题 · 离线可用 · 可搜索正文和错误代码";
        if (results.Count == 0)
        {
            ShowEmpty();
        }
    }

    private void Article_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedArticle is not { } article)
        {
            return;
        }
        ArticleTitle.Text = article.Title;
        ArticleSummary.Text = article.Summary;
        ArticleBody.Document = HelpDocumentRenderer.Render(article.Markdown);
        CopyButton.IsEnabled = true;
        RelatedTopics.Children.Clear();
        foreach (var id in article.Related)
        {
            var next = content.Get(id);
            var button = new Button { Content = next.Title, Margin = new(0, 0, 8, 5) };
            button.Click += (_, _) => Open(next.Id);
            RelatedTopics.Children.Add(button);
        }
        if (compact)
        {
            SetCatalogVisible(false);
        }
    }

    private void ShowEmpty()
    {
        ArticleTitle.Text = "没有找到相关帮助";
        ArticleSummary.Text = "请缩短关键词，或选择全部主题后再试。";
        ArticleBody.Document = HelpDocumentRenderer.Render("## 搜索方法\n\n输入功能名称、菜单名称或日志中的关键字。多个词以空格分隔，例如 ESP-IDF XTENSA_GNU_CONFIG。\n\n点击“清除搜索”可恢复完整目录。");
        RelatedTopics.Children.Clear();
        CopyButton.IsEnabled = false;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Refresh();
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => Open("quick-start");
    private void CatalogToggle_Click(object sender, RoutedEventArgs e) => SetCatalogVisible(CatalogPanel.Visibility != Visibility.Visible);
    private void SetCatalogVisible(bool visible)
    {
        CatalogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CatalogColumn.Width = visible ? new GridLength(compact ? 210 : 246) : new GridLength(0);
    }
    private void CopyArticle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedArticle is not { } article)
        {
            return;
        }
        try
        {
            Clipboard.SetText(article.Markdown);
            ResultStatus.Text = "已复制：“" + article.Title + "”。";
        }
        catch (System.Runtime.InteropServices.COMException error) { ResultStatus.Text = "剪贴板暂时不可用，请重试：" + error.Message; }
    }
}

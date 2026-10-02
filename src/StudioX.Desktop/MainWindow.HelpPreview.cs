namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    public async Task RenderHelpPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        var catalog = services.Help;
        Check(catalog.Articles.Count == 37 && catalog.Categories.Count == 6, "37 complete offline topics in six categories");
        foreach (var article in catalog.Articles)
        {
            Check(article.Markdown.Length > 400 && article.Markdown.Split('\n').Count(line => line.StartsWith("## ", StringComparison.Ordinal)) >= 4, article.Id + " has substantive instructions");
            var document = HelpDocumentRenderer.Render(article.Markdown);
            Check(document.Blocks.Count >= 8 && new TextRange(document.ContentStart, document.ContentEnd).Text.Length > 250, article.Id + " renders readable content");
        }
        Check(catalog.Search("ESP-IDF XTENSA_GNU_CONFIG").First().Id == "esp-idf-errors", "multi-term error search finds the ESP-IDF procedure");
        Check(catalog.Search("位与").First().Id == "programmer-assistant", "Chinese function search finds programmer assistant");
        Check(catalog.Search("CTRL+S").First().Id == "editor", "keyboard search ignores case");
        Check(catalog.Search("", "调试与设备").Count == 6, "category filter shows only matching device topics including connection guidance");
        Check(catalog.Search("no_such_help_topic_917").Count == 0, "unknown terms have a real empty state");
        Check(catalog.DiagnosticTopic("CMake Error: git-data/head-ref") == "esp-idf-errors", "first-commit failure links to the correct article");
        Check(TroubleshootingService.Explain("target=esp32s3 undefined reference to uart_init").Title == "链接符号缺失或重复", "compiler target metadata does not masquerade as a hardware failure");
        Check(TroubleshootingService.Explain("XTENSA_GNU_CONFIG pointed different files").Title == "Xtensa 动态配置冲突", "Xtensa errors have a dedicated actionable summary");
        await ShowHelpAsync();
        Check(projectDirectory is null && helpCenter!.SelectedArticle?.Id == "quick-start", "help opens without an engineering project");
        var originalTab = helpTab!;
        await CloseWorkspaceTabAsync(originalTab);
        await ShowHelpAsync("shortcuts");
        Check(helpTab == originalTab && helpTab.Visibility == Visibility.Visible && helpCenter!.SelectedArticle?.Id == "shortcuts", "closed help tab reopens without duplication");
        var helpBinding = InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.F1);
        helpBinding.Command.Execute(null);
        Check(helpCenter!.SelectedArticle?.Id == "quick-start", "F1 invokes the real help command");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await ShowHelpAsync("quick-start");
            await SettleAsync();
            Check(helpCenter.ArticleBody.ActualHeight > 150 && helpCenter.ArticleBody.ActualWidth > 450, theme.Id + " leaves a readable scrolling article area");
            Check(((System.Windows.Media.SolidColorBrush)helpCenter.ArticleList.Foreground).Color == ((System.Windows.Media.SolidColorBrush)FindResource("Text")).Color, theme.Id + " topic list follows the actual theme foreground");
            Render(this, Path.Combine(directory, "help-" + theme.Id + ".png"));
            helpCenter.Filter("ESP-IDF XTENSA_GNU_CONFIG");
            await SettleAsync();
            Check(helpCenter.SelectedArticle?.Id == "esp-idf-errors" && helpCenter.ArticleList.Items.Count >= 1, theme.Id + " full-text search displays the expected article");
            Render(this, Path.Combine(directory, "help-search-" + theme.Id + ".png"));
            helpCenter.Filter("no_such_help_topic_917");
            Check(helpCenter.ArticleList.Items.Count == 0 && !helpCenter.CopyButton.IsEnabled, theme.Id + " empty search does not show stale content");
        }
        ApplyTheme(ThemeService.Dark);
        Width = MinWidth;
        Height = 720;
        await ShowHelpAsync("programmer-assistant");
        await SettleAsync();
        Check(helpCenter.ArticleBody.ActualWidth > 350 && helpCenter.CatalogPanel.Visibility == Visibility.Collapsed, "compact window prioritizes article content and retains the directory toggle");
        Render(this, Path.Combine(directory, "help-compact.png"));
        await ShowHelpAsync("shortcuts");
        await SettleAsync();
        Check(helpCenter.ArticleBody.Document.Blocks.OfType<Table>().Count() == 4, "shortcut reference has four real formatted tables");
        Render(this, Path.Combine(directory, "help-shortcuts.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "help-result.json"), JsonSerializer.Serialize(new { status = "passed", topics = catalog.Articles.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));

        async Task SettleAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}

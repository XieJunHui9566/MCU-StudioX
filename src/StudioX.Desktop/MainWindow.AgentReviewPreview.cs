namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>使用真实审阅控件和工作台主题；不调用模型、MCP 或文件应用服务。</summary>
    public async Task RenderAgentReviewPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string text)
        {
            if (!condition)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
        }
        const string original = """
            #include <stdint.h>

            // 森林监测：保留完整上下文，核对报警阈值与采样逻辑。
            #define SMOKE_THRESHOLD 50

            static uint16_t read_smoke(void);
            static void set_alarm(int enabled);

            int main(void)
            {
                uint16_t smoke = 1;
                while (1)
                {
                    smoke = read_smoke();
                    if (smoke > SMOKE_THRESHOLD)
                    {
                        set_alarm(1);
                    }
                }
            }
            """;
        var matches = new[]
        {
            new TextSearchMatch(original.IndexOf("50", StringComparison.Ordinal), 2, "55"),
            new TextSearchMatch(original.IndexOf("= 1", StringComparison.Ordinal) + 2, 1, "0")
        };
        var main = new WorkspaceFileChange(new("src/main.c", original, Encoding.UTF8, "preview", false), original,
            WorkspaceEditService.ApplyText(original, matches), matches, true);
        const string header = "#pragma once\n// 采样周期\n#define SAMPLE_PERIOD_MS 100\n";
        var headerMatches = new[] { new TextSearchMatch(header.IndexOf("100", StringComparison.Ordinal), 3, "200") };
        var second = new WorkspaceFileChange(new("include/config.h", header, Encoding.UTF8, "preview", false), header,
            WorkspaceEditService.ApplyText(header, headerMatches), headerMatches, false);
        var plan = new AgentEditPlan("review-preview", "task-preview", "调整报警阈值、初值和采样周期", [main, second]);
        var review = new AgentChangesView();
        review.BindTypography(SourceEditor);
        IReadOnlyList<WorkspaceFileChange> submitted = [];
        review.ApplyRequested = _ => { submitted = review.Selected(plan); return Task.CompletedTask; };
        var tab = AddToolTab("AI 修改", review);
        ShowDocument(tab);
        review.Refresh("AI 修改审阅 · 离线界面验收", "尚未编译 / 未连接硬件", [plan]);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var picker = Descendants(review).OfType<ComboBox>().Single();
        Check(picker.Items.Count == 2 && review.Comparison.Before.Text == original && review.Comparison.After.Text == main.After,
            "多文件计划默认显示完整原文和计划结果");
        var toggles = Descendants(review).OfType<CheckBox>().Where(c => c.IsEnabled).ToArray();
        Check(toggles.Length == 2, "每个修改块可独立选择");
        toggles[1].IsChecked = false;
        var chosen = review.Selected(plan).Single(c => c.Path == main.Path);
        Check(chosen.Matches.Count == 1 && review.Comparison.After.Text == chosen.After && chosen.After.Contains("smoke = 1", StringComparison.Ordinal),
            "取消修改块立即更新对照，与应用所选修改使用同一份结果");
        Descendants(review).OfType<Button>().Single(b => Equals(b.Content, "应用所选修改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(submitted.Count == 2 && submitted.Single(c => c.Path == main.Path).Matches.Count == 1,
            "实际按钮回调只提交勾选的块，其他文件的选择保留");
        picker.SelectedIndex = 1;
        Check(review.Comparison.Before.Text == header && review.Comparison.After.Text == second.After, "切换文件替换两侧快照");
        picker.SelectedIndex = 0;
        Check(review.Comparison.After.Text == chosen.After, "切回文件保留逐块选择");
        review.Refresh("刷新后的任务", "尚未编译", [plan]);
        Check(review.Selected(plan).Single(c => c.Path == main.Path).Matches.Count == 1, "刷新计划保留用户选择且控件可以安全重新挂载");
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        foreach (var toggle in Descendants(review).OfType<CheckBox>().Where(c => c.IsEnabled).ToArray())
        {
            toggle.IsChecked = true;
        }
        var custom = ThemeService.Dark with
        {
            Id = "studiox.review-custom",
            DisplayName = "Review custom",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        Width = 1500;
        Height = 980;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
        {
            ApplyTheme(theme);
            foreach (var size in new[] { 13d, 18d })
            {
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = size;
                SourceEditor.FontSize = size;
                review.RefreshTheme();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Check(review.Comparison.Before.FontSize == size && review.Comparison.After.FontSize == size,
                    theme.Id + "/" + size + "：已打开的对照跟随源码字号");
                var hunkList = Descendants(review).OfType<ListBox>().Single(list =>
                    System.Windows.Automation.AutomationProperties.GetName(list) == "选择修改块并定位");
                Check(Equals(hunkList.Background, FindResource("EditorSurface")) && Equals(hunkList.Foreground, FindResource("Text")),
                    theme.Id + "/" + size + "：修改块列表与代码区背景及主题文字色一致，无默认白底");
                foreach (var editor in new[] { review.Comparison.Before, review.Comparison.After })
                {
                    using var highlighter = new DocumentHighlighter(editor.Document, editor.SyntaxHighlighting!);
                    Check(highlighter.HighlightLine(9).Sections.Any(s => s.Color.Name == "Type") &&
                        highlighter.HighlightLine(3).Sections.Any(s => s.Color.Name == "Comment"),
                        theme.Id + "/" + size + "：两侧实际语法着色保留类型与注释颜色");
                    Check(editor.ActualWidth > 250 && editor.ActualHeight > 180 && editor.TextArea.TextView.VisualLinesValid,
                        theme.Id + "/" + size + "：两侧代码区域有界且完成实际渲染");
                }
                Check(review.Comparison.Comparison.Changes.Count == 2, theme.Id + "/" + size + "：差异标记不随主题或字号丢失");
                Check(review.Comparison.Before.TextArea.TextView.BackgroundRenderers.OfType<CodeComparisonRenderer>().Single().LineOpacity >= .22 &&
                    review.Comparison.After.TextArea.TextView.BackgroundRenderers.OfType<CodeComparisonRenderer>().Single().CharacterOpacity >= .38,
                    theme.Id + "/" + size + "：红绿变更行和变化字符使用用户要求的增强亮度");
                Render(this, Path.Combine(directory, theme.Id + "-" + size + ".png"));
            }
        }
        Width = 1060;
        Height = 820;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var beforeBounds = review.Comparison.Before.TransformToAncestor(review).TransformBounds(new Rect(review.Comparison.Before.RenderSize));
        var afterBounds = review.Comparison.After.TransformToAncestor(review).TransformBounds(new Rect(review.Comparison.After.RenderSize));
        Render(this, Path.Combine(directory, "compact-large.png"));
        await JsonStore.WriteAsync(Path.Combine(directory, "compact-layout.json"), new
        {
            window = new
            {
                ActualWidth,
                ActualHeight
            },
            review = new
            {
                review.ActualWidth,
                review.ActualHeight
            },
            comparison = new
            {
                review.Comparison.ActualWidth,
                review.Comparison.ActualHeight
            },
            beforeBounds,
            afterBounds
        });
        Check(afterBounds.Left > beforeBounds.Right && beforeBounds.Height > 100 && afterBounds.Height > 100,
            "较窄窗口将计划列表移至顶部，为放大字体的左右对照保留可用空间");
        Height = 1000;
        review.MaxWidth = 620;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        beforeBounds = review.Comparison.Before.TransformToAncestor(review).TransformBounds(new Rect(review.Comparison.Before.RenderSize));
        afterBounds = review.Comparison.After.TransformToAncestor(review).TransformBounds(new Rect(review.Comparison.After.RenderSize));
        Check(afterBounds.Top > beforeBounds.Bottom && beforeBounds.Height > 70 && afterBounds.Height > 70,
            "更窄的审阅区域自动上下对照，放大字体时两侧仍可滚动查看");
        Render(this, Path.Combine(directory, "stacked-large.png"));
        await CheckAgentReviewScrollingAsync(directory, Check);
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = true,
            hardware = false,
            paidApi = false,
            checks
        });
    }
}

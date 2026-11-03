namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在真实 WPF 布局中验证长文件滚动；不能以无滚动范围的短示例替代。</summary>
    private async Task CheckAgentReviewScrollingAsync(string directory, Action<bool, string> check)
    {
        var original = string.Concat(Enumerable.Range(1, 900)
            .Select(line => $"static unsigned value_{line:D4} = {line}; // 保留上下文 {new string('x', 120)}\n"));
        var insertion = original.IndexOf("static unsigned value_0151", StringComparison.Ordinal);
        var removalStart = original.IndexOf("static unsigned value_0401", StringComparison.Ordinal);
        var removalEnd = original.IndexOf("static unsigned value_0441", StringComparison.Ordinal);
        var matches = new[]
        {
            new TextSearchMatch(insertion, 0, string.Concat(Enumerable.Range(1, 80)
                .Select(line => $"static unsigned inserted_{line:D4} = {line}; // 新增区域\n"))),
            new TextSearchMatch(removalStart, removalEnd - removalStart, "")
        };
        var change = new WorkspaceFileChange(new("src/long-review.c", original, Encoding.UTF8, "scroll-preview", false),
            original, WorkspaceEditService.ApplyText(original, matches), matches, true);
        var plan = new AgentEditPlan("review-scroll-preview", "review-scroll-task", "长文件插入与删除滚动验收", [change]);
        var review = new AgentChangesView();
        review.BindTypography(SourceEditor);
        var tab = AddToolTab("AI 修改 · 长文件滚动", review);
        ShowDocument(tab);
        review.Refresh("AI 修改审阅 · 长文件滚动", "离线界面验收", [plan]);
        Width = 1500;
        Height = 980;
        review.MaxWidth = double.PositiveInfinity;
        SourceEditor.FontSize = 13;
        await SettleAsync();
        var before = review.Comparison.Before;
        var after = review.Comparison.After;
        var observations = new List<object>();
        var movements = new List<object>();
        foreach (var editor in new[] { before, after })
        {
            editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            {
                if (movements.Count < 300)
                {
                    movements.Add(new { side = editor == before ? "before" : "after", editor.VerticalOffset,
                        view = editor.TextArea.TextView.ScrollOffset.Y, horizontal = editor.HorizontalOffset });
                }
            };
        }
        check(!review.Comparison.Comparison.IsCoarse && review.Comparison.Comparison.Changes.Count == 2,
            "长文件夹具保留独立新增与删除块，不能以粗略整块映射掩盖同步故障");
        await JsonStore.WriteAsync(Path.Combine(directory, "scroll-initial-layout.json"), new
        {
            review.ActualWidth, review.ActualHeight,
            editors = new[] { before, after }.Select(editor => new
            {
                editor.ActualWidth, editor.ActualHeight, editor.FontSize, editor.Document.LineCount,
                ScrollViewerFor(editor).ScrollableHeight, ScrollViewerFor(editor).ScrollableWidth,
                ScrollViewerFor(editor).ViewportHeight, ScrollViewerFor(editor).ExtentHeight
            }).ToArray()
        });
        foreach (var editor in new[] { before, after })
        {
            var scroll = ScrollViewerFor(editor);
            check(scroll.ScrollableHeight > editor.ActualHeight * 10,
                "长文件两侧均有真实纵向滚动范围");
        }

        // 处在新增块内部时，原文多个位置映射到同一行，不能由从侧的延迟事件反向拉回。
        var insertedMiddle = after.Document.GetLineByOffset(insertion).LineNumber + 40;
        await ScrollAndCheckAsync(after, insertedMiddle, "新增块内部可以连续向下浏览，不被原文反向拉回");
        if (SystemParameters.WheelScrollLines != 0)
        {
            var wheelOffset = after.VerticalOffset;
            await NativeWheelAsync(after, -120);
            check(after.VerticalOffset > wheelOffset,
                "原生 HWND 滚轮路由在新增块内部向下生效，不要求代码区持有键盘焦点");
            await NativeWheelAsync(after, 120);
            check(Math.Abs(after.VerticalOffset - wheelOffset) < after.TextArea.TextView.DefaultLineHeight,
                "原生 HWND 滚轮可向上返回，不发生双重滚动或异步回弹");
            wheelOffset = after.VerticalOffset;
            await NativeWheelAsync(after, -60);
            check(Math.Abs(after.VerticalOffset - wheelOffset) < 1, "高精度半格滚轮不会提前整格滚动");
            await NativeWheelAsync(after, -60);
            check(after.VerticalOffset > wheelOffset, "高精度滚轮累积完整一格后可继续浏览新增区域");
        }
        await ScrollAndCheckAsync(before, 420, "被删除区域内部仍能浏览原文，不被结果侧拉回");
        await ScrollAndCheckAsync(after, 740, "修改后长文件可以滚动至下半部");
        var beforeViewer = ScrollViewerFor(before);
        var priorOffset = before.VerticalOffset;
        beforeViewer.LineDown();
        await SettleAsync();
        await JsonStore.WriteAsync(Path.Combine(directory, "scroll-progress.json"), new { priorOffset,
            actual = before.VerticalOffset, view = before.TextArea.TextView.ScrollOffset.Y, observations, movements });
        check(before.VerticalOffset > priorOffset, "真实 ScrollViewer 行滚动命令向下生效，布局完成后不回弹");
        CheckMapped(before, after, true, "从原文滚动后，结果侧按实际变化位置同步");

        foreach (var editor in new[] { before, after })
        {
            if (editor == before) { editor.ScrollToEnd(); }
            else { editor.ScrollToVerticalOffset(double.MaxValue); }
            await SettleAsync();
            var scroll = ScrollViewerFor(editor);
            await JsonStore.WriteAsync(Path.Combine(directory, editor == before ? "scroll-before-end.json" : "scroll-after-end.json"),
                new { editor.VerticalOffset, scroll.ScrollableHeight, view = editor.TextArea.TextView.ScrollOffset.Y, movements });
            check(Math.Abs(editor.VerticalOffset - scroll.ScrollableHeight) < 1,
                "两侧均能拖动或翻页至真实文件末尾");
            var endOffset = editor.VerticalOffset;
            scroll.LineUp();
            await SettleAsync();
            check(editor.VerticalOffset < endOffset, "到文件末尾后仍能继续向上滚动");
        }

        await ScrollAndCheckAsync(after, 740, "为刷新回归保留已浏览的中后部位置");
        check(ScrollViewerFor(before).ScrollableWidth > 100 && ScrollViewerFor(after).ScrollableWidth > 100,
            "两侧长行均有真实横向滚动范围");
        after.ScrollToHorizontalOffset(260);
        await SettleAsync();
        var retainedBefore = before.VerticalOffset;
        var retainedAfter = after.VerticalOffset;
        var retainedHorizontal = after.HorizontalOffset;
        review.Refresh("同一任务的状态更新", "仅状态变化，不重定位正文", [plan]);
        await SettleAsync();
        await JsonStore.WriteAsync(Path.Combine(directory, "scroll-refresh.json"), new { retainedBefore, retainedAfter,
            retainedHorizontal, before = before.VerticalOffset, after = after.VerticalOffset, horizontal = after.HorizontalOffset });
        check(Math.Abs(before.VerticalOffset - retainedBefore) < 1 && Math.Abs(after.VerticalOffset - retainedAfter) < 1 &&
            Math.Abs(after.HorizontalOffset - retainedHorizontal) < 1,
            "同一计划状态刷新保留两侧纵向和横向浏览位置");

        var custom = ThemeService.Dark with
        {
            Id = "studiox.scroll-custom", DisplayName = "Scroll custom",
            Colors = new(ThemeService.Dark.Colors) { ["Accent"] = "#B8E986", ["OnAccent"] = "#163020" }
        };
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
        {
            ApplyTheme(theme);
            foreach (var fontSize in new[] { 13d, 18d })
            {
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = fontSize;
                SourceEditor.FontSize = fontSize;
                review.RefreshTheme();
                await SettleAsync();
                await ScrollAndCheckAsync(after, 740, theme.Id + "/" + fontSize + "：主题和较大字号下仍可浏览长文件");
                var line = (int)(after.VerticalOffset / after.TextArea.TextView.DefaultLineHeight);
                Width = 1400;
                await SettleAsync();
                check((int)(after.VerticalOffset / after.TextArea.TextView.DefaultLineHeight) >= line - 1,
                    theme.Id + "/" + fontSize + "：调整窗口尺寸不将正文拉回修改处的光标");
                Width = 1500;
                await SettleAsync();
                Render(this, Path.Combine(directory, "scroll-" + theme.Id + "-" + fontSize + ".png"));
            }
        }

        Height = 1000;
        review.MaxWidth = 620;
        await SettleAsync();
        var beforeBounds = before.TransformToAncestor(review).TransformBounds(new Rect(before.RenderSize));
        var afterBounds = after.TransformToAncestor(review).TransformBounds(new Rect(after.RenderSize));
        check(afterBounds.Top > beforeBounds.Bottom, "窄布局实际使用上下对照");
        await ScrollAndCheckAsync(after, insertedMiddle, "上下对照仍能浏览新增块内部");
        await ScrollAndCheckAsync(before, 420, "上下对照仍能浏览被删区域内部");
        await ScrollAndCheckAsync(after, 820, "上下对照仍能浏览文件下部");
        Render(this, Path.Combine(directory, "scroll-stacked-large.png"));
        await JsonStore.WriteAsync(Path.Combine(directory, "scroll-observations.json"), new
        {
            hardware = false, paidApi = false, originalLines = before.Document.LineCount,
            resultLines = after.Document.LineCount, observations
        });

        async Task SettleAsync()
        {
            // ScrollViewer 滚动命令在布局中执行；必须跨多个分发器轮次检查，而非只检查调用栈。
            for (var pass = 0; pass < 4; pass++)
            {
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }

        async Task ScrollAndCheckAsync(TextEditor editor, int line, string description)
        {
            var requested = (line - 1) * editor.TextArea.TextView.DefaultLineHeight;
            editor.ScrollToVerticalOffset(requested);
            await SettleAsync();
            var expected = Math.Min(requested, ScrollViewerFor(editor).ScrollableHeight);
            observations.Add(new { description, requested = expected, actual = editor.VerticalOffset,
                before = before.VerticalOffset, after = after.VerticalOffset });
            check(Math.Abs(editor.VerticalOffset - expected) <= editor.TextArea.TextView.DefaultLineHeight,
                description);
            CheckMapped(editor, editor == before ? after : before, editor == before, description + "：另一侧位置同步");
            check(new[] { before, after }.All(pane => Math.Abs(pane.VerticalOffset - pane.TextArea.TextView.VerticalOffset) < 1),
                description + "：实际视口与滚动条位置一致");
        }

        async Task NativeWheelAsync(TextEditor editor, int delta)
        {
            var screen = editor.TextArea.TextView.PointToScreen(new Point(60, 40));
            var coordinates = unchecked((nint)(((int)screen.Y & 0xffff) << 16 | ((int)screen.X & 0xffff)));
            var keys = unchecked((nint)((delta & 0xffff) << 16));
            SendPreviewWheel(new WindowInteropHelper(this).Handle, 0x020A, keys, coordinates);
            await SettleAsync();
        }

        void CheckMapped(TextEditor source, TextEditor target, bool fromBefore, string description)
        {
            var sourceLine = Math.Clamp((int)(source.VerticalOffset / source.TextArea.TextView.DefaultLineHeight) + 1,
                1, source.Document.LineCount);
            var mappedOffset = review.Comparison.MapOffset(source.Document.GetLineByNumber(sourceLine).Offset, fromBefore);
            var targetLine = target.Document.GetLineByOffset(Math.Clamp(mappedOffset, 0, target.Document.TextLength)).LineNumber;
            var expected = Math.Min((targetLine - 1) * target.TextArea.TextView.DefaultLineHeight,
                ScrollViewerFor(target).ScrollableHeight);
            check(Math.Abs(target.VerticalOffset - expected) <= target.TextArea.TextView.DefaultLineHeight * 2, description);
        }

        static ScrollViewer ScrollViewerFor(TextEditor editor) => Descendants(editor).OfType<ScrollViewer>().Single();
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application.Editing;

/// <summary>完整快照的只读对照；只为当前文件创建两份可视文档。</summary>
internal sealed class CodeComparisonView : Grid
{
    internal SafeTextEditor Before { get; } = CreateEditor("修改前的完整源码");
    internal SafeTextEditor After { get; } = CreateEditor("修改后的完整源码");
    internal CodeComparison Comparison { get; private set; } = new([], false);
    internal string Status => status.Text;
    internal event Action<string, Exception>? HighlightingFailed;
    private readonly CodeComparisonRenderer removed = new();
    private readonly CodeComparisonRenderer added = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    private readonly Grid panes = new();
    private readonly Grid left;
    private readonly Grid right;
    private string path = "";
    private bool syncing;
    private Vector beforeScrollOffset;
    private Vector afterScrollOffset;

    internal CodeComparisonView()
    {
        RowDefinitions.Add(new());
        RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        panes.ColumnDefinitions.Add(new());
        panes.ColumnDefinitions.Add(new()
        {
            Width = new(8)
        });
        panes.ColumnDefinitions.Add(new());
        panes.RowDefinitions.Add(new());
        panes.RowDefinitions.Add(new()
        {
            Height = new(0)
        });
        panes.RowDefinitions.Add(new()
        {
            Height = new(0)
        });
        left = Pane("修改前 · 删除 / 被替换内容", "DiagnosticError", Before);
        right = Pane("修改后 · 新增 / 替换结果", "DiagnosticSuccess", After);
        panes.Children.Add(left);
        SetColumn(right, 2);
        panes.Children.Add(right);
        Children.Add(panes);
        SetRow(status, 1);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Children.Add(status);
        Before.TextArea.TextView.BackgroundRenderers.Add(removed);
        After.TextArea.TextView.BackgroundRenderers.Add(added);
        Before.TextArea.TextView.ScrollOffsetChanged += (_, _) => Synchronize(Before, After, true);
        After.TextArea.TextView.ScrollOffsetChanged += (_, _) => Synchronize(After, Before, false);
        Before.HighlightingFailed += (language, exception) => HighlightingFailed?.Invoke(language, exception);
        After.HighlightingFailed += (language, exception) => HighlightingFailed?.Invoke(language, exception);
        SizeChanged += (_, _) => ArrangePanes();
        Loaded += (_, _) => RefreshTheme();
    }

    internal void BindTypography(TextEditor source)
    {
        foreach (var editor in new[] { Before, After })
        {
            foreach (var property in new[] { TextEditor.FontFamilyProperty, TextEditor.FontSizeProperty, TextEditor.FontWeightProperty, TextEditor.FontStyleProperty })
            {
                editor.SetBinding(property, new Binding(property.Name) { Source = source });
            }
        }
    }

    internal bool Show(string file, string before, string after)
    {
        // 状态刷新仍显示相同快照时，不重建文档或清空用户正在浏览的位置。
        if (path == file && Before.Text == before && After.Text == after)
        {
            return false;
        }
        var comparison = CodeComparison.Create(before, after);
        path = file;
        Comparison = comparison;
        syncing = true;
        try
        {
            Before.Text = before;
            After.Text = after;
            removed.Ranges = comparison.Changes.Select(c => new TextSegment { StartOffset = c.BeforeStart, Length = c.BeforeLength }).ToArray();
            added.Ranges = comparison.Changes.Select(c => new TextSegment { StartOffset = c.AfterStart, Length = c.AfterLength }).ToArray();
            status.Text = comparison.Changes.Count == 0 ? "前后内容相同。" :
                $"{comparison.Changes.Count} 处差异 · 深底色标记变化字符，细线标记增删位置。" +
                (comparison.IsCoarse ? " 大幅变化已按整块标记，全文仍可查看。" : "");
            Before.ScrollToHome();
            After.ScrollToHome();
            RefreshTheme();
        }
        finally { syncing = false; }
        return true;
    }

    internal void FocusChange(int beforeOffset, int afterOffset)
    {
        syncing = true;
        try
        {
            Focus(Before, beforeOffset);
            Focus(After, afterOffset);
        }
        finally { syncing = false; }
        static void Focus(TextEditor editor, int offset)
        {
            editor.CaretOffset = Math.Clamp(offset, 0, editor.Document.TextLength);
            editor.ScrollTo(editor.TextArea.Caret.Line, editor.TextArea.Caret.Column);
        }
    }

    internal void RefreshTheme()
    {
        var background = (TryFindResource("Background") as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 140;
        foreach (var editor in new[] { Before, After })
        {
            editor.SyntaxHighlighting = CodeLanguage.Get(CodeLanguage.ForFile(path), dark);
            editor.TextArea.SelectionForeground = null;
            editor.TextArea.TextView.Redraw();
        }
        removed.Color = (Brush?)TryFindResource("DiagnosticError") ?? Brushes.Transparent;
        added.Color = (Brush?)TryFindResource("DiagnosticSuccess") ?? Brushes.Transparent;
        foreach (var renderer in new[] { removed, added })
        {
            renderer.LineOpacity = dark ? .30 : .22;
            renderer.CharacterOpacity = dark ? .52 : .38;
        }
    }

    private void Synchronize(TextEditor source, TextEditor target, bool fromBefore)
    {
        var sourceView = source.TextArea.TextView;
        var position = sourceView.ScrollOffset;
        var previous = fromBefore ? beforeScrollOffset : afterScrollOffset;
        if (fromBefore)
        {
            beforeScrollOffset = position;
        }
        else
        {
            afterScrollOffset = position;
        }
        if (syncing || !source.IsLoaded || source.TextArea.TextView.DefaultLineHeight <= 0)
        {
            return;
        }
        syncing = true;
        try
        {
            // 此事件先于 ScrollViewer 更新缓存；必须读取刚变化的 TextView 偏移。
            // 翻页和到文件尾可短暂传入越界值；先在本侧夹紧，防止空白视口改变滚动条布局。
            var sourceScroll = (IScrollInfo)sourceView;
            var vertical = Math.Clamp(position.Y, 0, Math.Max(0, sourceScroll.ExtentHeight - sourceScroll.ViewportHeight));
            var horizontal = Math.Clamp(position.X, 0, Math.Max(0, sourceScroll.ExtentWidth - sourceScroll.ViewportWidth));
            if (position.Y != vertical)
            {
                sourceScroll.SetVerticalOffset(vertical);
            }
            if (position.X != horizontal)
            {
                sourceScroll.SetHorizontalOffset(horizontal);
            }
            sourceScroll.ScrollOwner?.InvalidateScrollInfo();
            position = sourceView.ScrollOffset;
            var line = Math.Clamp((int)(position.Y / sourceView.DefaultLineHeight) + 1, 1, source.Document.LineCount);
            var offset = source.Document.GetLineByNumber(line).Offset;
            var mapped = MapOffset(offset, fromBefore);
            var targetLine = target.Document.GetLineByOffset(Math.Clamp(mapped, 0, target.Document.TextLength)).LineNumber;
            var fraction = position.Y % sourceView.DefaultLineHeight;
            // ScrollViewer 的 ScrollTo 命令延迟到布局执行，会在 syncing 复位后反向同步。
            // 直接设置 TextView 的像素滚动接口，让从侧事件在当前保护范围内完成。
            var scroll = (IScrollInfo)target.TextArea.TextView;
            if (position.Y != previous.Y)
            {
                // 预先限制从侧范围，避免文件尾的布局裁剪再触发一次反向同步。
                scroll.SetVerticalOffset(Math.Clamp((targetLine - 1) * target.TextArea.TextView.DefaultLineHeight + fraction,
                    0, Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight)));
            }
            if (position.X != previous.X)
            {
                // 横向范围随可见行变化；单独的横向裁剪不能把纵向浏览位置拉回。
                scroll.SetHorizontalOffset(Math.Clamp(position.X, 0, Math.Max(0, scroll.ExtentWidth - scroll.ViewportWidth)));
            }
            // 直接设置偏移不会自动刷新 ScrollViewer 缓存，滚动条及后续滚轮路由也须使用新值。
            scroll.ScrollOwner?.InvalidateScrollInfo();
        }
        finally { syncing = false; }
    }

    internal int MapOffset(int offset, bool fromBefore)
    {
        var delta = 0;
        foreach (var change in Comparison.Changes)
        {
            var start = fromBefore ? change.BeforeStart : change.AfterStart;
            var length = fromBefore ? change.BeforeLength : change.AfterLength;
            var targetStart = fromBefore ? change.AfterStart : change.BeforeStart;
            var targetLength = fromBefore ? change.AfterLength : change.BeforeLength;
            if (offset < start)
            {
                break;
            }
            if (offset < start + length)
            {
                return targetStart + Math.Min(offset - start, targetLength);
            }
            delta = targetStart + targetLength - start - length;
        }
        return offset + delta;
    }

    private void ArrangePanes()
    {
        var compact = ActualWidth < 640;
        panes.ColumnDefinitions[1].Width = new(compact ? 0 : 8);
        panes.ColumnDefinitions[2].Width = compact ? new(0) : new(1, GridUnitType.Star);
        panes.RowDefinitions[1].Height = new(compact ? 8 : 0);
        panes.RowDefinitions[2].Height = compact ? new(1, GridUnitType.Star) : new(0);
        SetColumn(right, compact ? 0 : 2);
        SetRow(right, compact ? 2 : 0);
    }

    private static SafeTextEditor CreateEditor(string name)
    {
        var editor = new SafeTextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = new("Cascadia Mono, Consolas"),
            Padding = new(4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        editor.SetResourceReference(TextEditor.FontSizeProperty, "WorkbenchFontSize");
        editor.SetResourceReference(BackgroundProperty, "EditorSurface");
        editor.SetResourceReference(TextEditor.ForegroundProperty, "Text");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Muted");
        editor.Document.UndoStack.SizeLimit = 0;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = false;
        System.Windows.Automation.AutomationProperties.SetName(editor, name);
        return editor;
    }

    private static Grid Pane(string title, string color, TextEditor editor)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new());
        var label = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 5) };
        label.SetResourceReference(TextBlock.ForegroundProperty, color);
        grid.Children.Add(label);
        var border = new Border { BorderThickness = new(1), Child = editor };
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        SetRow(border, 1);
        grid.Children.Add(border);
        return grid;
    }
}

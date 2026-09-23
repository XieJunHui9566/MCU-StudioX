namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private readonly DispatcherTimer outlineTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? outlineCancellation;
    private Task outlineTask = Task.CompletedTask;
    private long outlineRevision;
    private TextDocument? outlineDocument;
    private string outlineText = "";
    private IReadOnlyList<CodeDocumentSymbol> outlineSymbols = [];
    private double savedOutlineWidth = 270;
    private readonly HashSet<CancellationTokenSource> outlineDetailRequests = [];

    private void InitializeOutline()
    {
        outlineTimer.Tick += (_, _) => { outlineTimer.Stop(); outlineTask = RefreshOutlineAsync(); };
        SourceEditor.IsVisibleChanged += (_, _) => QueueOutlineRefresh(clear: true);
    }

    private void CancelOutline()
    {
        outlineTimer.Stop(); outlineRevision++; outlineCancellation?.Cancel();
        foreach (var request in outlineDetailRequests) request.Cancel();
    }

    private void QueueOutlineRefresh(bool clear = false)
    {
        if (OutlineTree is null) return;
        CancelOutline();
        OutlineTree.IsEnabled = false;
        if (clear || activeEditor?.Buffer != outlineDocument)
        {
            outlineSymbols = []; outlineDocument = null; outlineText = "";
            OutlineTree.Items.Clear();
        }
        OutlineFileName.Text = activeDocument is { } source ? Path.GetFileName(source.RelativePath) : "";
        OutlineFileName.ToolTip = activeDocument?.RelativePath;
        if (closing || activeDocument is null || WorkspaceTabs.SelectedItem != EditorTab || OutlinePanel.Visibility != Visibility.Visible)
        { OutlineStatus.Text = "打开 C/C++ 文件查看结构"; return; }
        if (!CodeIntelligenceService.Supports(activeDocument.RelativePath))
        { OutlineStatus.Text = "当前文件类型暂不提供函数与变量列表"; return; }
        if (!services.Intelligence.IsReady)
        { OutlineStatus.Text = "语言服务尚未就绪"; return; }
        OutlineStatus.Text = "正在更新文件结构…";
        outlineTimer.Start();
    }

    private async Task RefreshOutlineAsync()
    {
        if (closing || activeEditor is not { } session || !services.Intelligence.IsReady || WorkspaceTabs.SelectedItem != session.Tab) return;
        var revision = outlineRevision;
        var document = session.Buffer; var text = document.Text; var path = session.Source.RelativePath;
        var snapshots = CaptureCodeDocuments();
        var cancellation = new CancellationTokenSource(); outlineCancellation = cancellation;
        bool Current() => !closing && !cancellation.IsCancellationRequested && revision == outlineRevision &&
            activeEditor == session && SourceEditor.Document == document && document.Text == text;
        try
        {
            // 解析和语言请求都在后台；过期返回不能覆盖已切换文件或更新后的缓冲区。
            var symbols = await Task.Run(() => services.Intelligence.DocumentSymbolsAsync(path, text, cancellation.Token, snapshots), cancellation.Token);
            if (!Current()) return;
            outlineDocument = document; outlineText = text; outlineSymbols = symbols;
            RenderOutline(); OutlineTree.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (Current()) { OutlineStatus.Text = "文件结构暂不可用，编辑或重新展开后重试"; OutlineStatus.ToolTip = ex.Message; Log("文件结构：" + ex); }
        }
        finally
        {
            if (outlineCancellation == cancellation) outlineCancellation = null;
            cancellation.Dispose();
        }
    }

    private void OutlineSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OutlineTree is not null && outlineDocument == SourceEditor.Document && outlineText == SourceEditor.Text) RenderOutline();
    }

    private void RenderOutline()
    {
        var query = OutlineSearch.Text.Trim();
        var expanded = new HashSet<string>();
        void Remember(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            { if (item.IsExpanded && item.Uid.Length > 0) expanded.Add(item.Uid); Remember(item); }
        }
        Remember(OutlineTree); OutlineTree.Items.Clear();
        var displayed = 0; var truncated = false;
        foreach (var (label, group) in new[] { ("函数", 0), ("变量", 1), ("类型与其他", 2) })
        {
            var root = new TreeViewItem { IsExpanded = true, Focusable = false };
            foreach (var symbol in outlineSymbols.Where(s => OutlineGroup(s.Kind) == group))
                if (MakeItem(symbol, "", false) is { } item) root.Items.Add(item);
            if (root.Items.Count == 0) continue;
            root.Header = new TextBlock { Text = $"{label} ({root.Items.Count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
            OutlineTree.Items.Add(root);
        }
        OutlineStatus.ToolTip = null;
        OutlineStatus.Text = displayed == 0 ? (query.Length > 0 ? "没有匹配的函数或变量" : "当前文件没有可显示的声明") :
            $"{displayed} 个符号 · 单击跳转" + (activeEditor?.IsDirty == true ? " · 含未保存修改" : "") + (truncated ? " · 仅显示前 3000 项" : "");

        TreeViewItem? MakeItem(CodeDocumentSymbol symbol, string parentKey, bool include)
        {
            if (displayed >= 3000) { truncated = true; return null; }
            var matches = include || query.Length == 0 || symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || symbol.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
            var key = parentKey + "/" + symbol.Kind + ":" + symbol.Name;
            var item = new TreeViewItem { Tag = symbol, Uid = key, Padding = new Thickness(2, 3, 2, 3) };
            foreach (var child in symbol.Children)
                if (MakeItem(child, key, matches) is { } childItem) item.Items.Add(childItem);
            if (!matches && item.Items.Count == 0) return null;
            if (symbol.Kind is 6 or 9 or 12 && symbol.Children.Count == 0)
            {
                item.Items.Add(new TreeViewItem { Header = "展开查看参数与局部变量", IsEnabled = false });
                item.Expanded += OutlineFunction_Expanded;
            }
            displayed++;
            var (glyph, color, kind) = OutlineStyle(symbol.Kind);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Width = 23, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = symbol.Name, VerticalAlignment = VerticalAlignment.Center });
            var line = new TextBlock { Text = (symbol.SelectionRange.Start.Line + 1).ToString(), FontSize = 11, Margin = new Thickness(9, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(line);
            item.Header = row;
            item.ToolTip = $"{kind}  {symbol.Name}" + (symbol.Detail.Length > 0 ? "\n" + symbol.Detail : "") + $"\n第 {symbol.SelectionRange.Start.Line + 1} 行";
            AutomationProperties.SetName(item, $"{kind} {symbol.Name}，第 {symbol.SelectionRange.Start.Line + 1} 行");
            item.IsExpanded = (symbol.Kind is not (6 or 9 or 12) && query.Length > 0) || expanded.Contains(key);
            return item;
        }
    }

    private static int OutlineGroup(int kind) => kind is 6 or 9 or 12 ? 0 : kind is 7 or 8 or 13 or 14 ? 1 : 2;
    private static (string Glyph, string Color, string Kind) OutlineStyle(int kind) => kind switch
    {
        6 or 9 or 12 => ("f", "#D5A859", "函数"),
        7 or 8 => ("m", "#58B9D8", "成员变量"),
        13 => ("v", "#62A2F5", "变量"),
        14 => ("c", "#AD8BE8", "常量"),
        5 or 10 or 11 or 23 or 26 => ("{}", "#63BC92", "类型"),
        22 => ("e", "#AD8BE8", "枚举项"),
        _ => ("#", "#AD8BE8", "声明")
    };

    private async void OutlineTree_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 折叠箭头只展开列表，点击条目文字才跳转。
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != OutlineTree; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ToggleButton) return;
            if (node is TreeViewItem { Tag: CodeDocumentSymbol symbol }) { e.Handled = true; await NavigateOutlineAsync(symbol); return; }
        }
    }
    private async void OutlineTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OutlineTree.SelectedItem is TreeViewItem { Tag: CodeDocumentSymbol symbol })
        { e.Handled = true; await NavigateOutlineAsync(symbol); }
    }
    private Task NavigateOutlineAsync(CodeDocumentSymbol symbol)
    {
        if (!OutlineTree.IsEnabled || activeDocument is null || outlineDocument != SourceEditor.Document || outlineText != SourceEditor.Text) return Task.CompletedTask;
        var path = activeDocument.RelativePath;
        return RunAsync(token => JumpToCodeAsync(new(path, symbol.SelectionRange, path), token));
    }

    private async void OutlineFunction_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: CodeDocumentSymbol function } item || e.OriginalSource != item ||
            activeDocument is null || outlineDocument != SourceEditor.Document || outlineText != SourceEditor.Text) return;
        item.Expanded -= OutlineFunction_Expanded;
        var revision = outlineRevision; var path = activeDocument.RelativePath; var text = outlineText;
        var snapshots = CaptureCodeDocuments(); var cancellation = new CancellationTokenSource();
        outlineDetailRequests.Add(cancellation);
        item.Items.Clear(); item.Items.Add(new TreeViewItem { Header = "正在读取局部变量…", IsEnabled = false });
        try
        {
            var variables = await Task.Run(() => services.Intelligence.FunctionVariablesAsync(path, text, function.Range, cancellation.Token, snapshots), cancellation.Token);
            if (closing || cancellation.IsCancellationRequested || revision != outlineRevision) return;
            item.Items.Clear();
            foreach (var variable in variables)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = "v", Foreground = new SolidColorBrush(Color.FromRgb(98, 162, 245)), FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Width = 23 });
                row.Children.Add(new TextBlock { Text = variable.Name });
                var line = new TextBlock { Text = (variable.Range.Start.Line + 1).ToString(), FontSize = 11, Margin = new Thickness(9, 0, 0, 0) };
                line.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); row.Children.Add(line);
                var child = new TreeViewItem { Header = row, Tag = variable, ToolTip = variable.Detail + $"\n第 {variable.Range.Start.Line + 1} 行", Padding = new Thickness(2, 3, 2, 3) };
                AutomationProperties.SetName(child, $"{variable.Detail.Split('·')[0].Trim()} {variable.Name}，第 {variable.Range.Start.Line + 1} 行");
                item.Items.Add(child);
            }
            if (variables.Count == 0) item.Items.Add(new TreeViewItem { Header = "没有参数或局部变量", IsEnabled = false });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!closing && !cancellation.IsCancellationRequested && revision == outlineRevision)
            { item.Items.Clear(); item.Items.Add(new TreeViewItem { Header = "局部变量暂不可用", ToolTip = ex.Message, IsEnabled = false }); Log("局部变量：" + ex); item.Expanded += OutlineFunction_Expanded; }
        }
        finally { outlineDetailRequests.Remove(cancellation); cancellation.Dispose(); }
    }

    private void ToggleOutline_Click(object sender, RoutedEventArgs e)
    {
        var hide = OutlinePanel.Visibility == Visibility.Visible;
        if (hide) savedOutlineWidth = Math.Max(190, OutlineColumn.ActualWidth);
        OutlineColumn.Width = new GridLength(hide ? 0 : savedOutlineWidth);
        OutlineSplitterColumn.Width = new GridLength(hide ? 0 : 4);
        OutlinePanel.Visibility = OutlineSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        QueueOutlineRefresh();
    }
}

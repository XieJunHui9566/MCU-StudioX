namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private CancellationTokenSource? hoverCancellation;
    private CancellationTokenSource? navigationCancellation;
    private Task hoverTask = Task.CompletedTask;
    private Task navigationTask = Task.CompletedTask;
    private ToolTip? symbolToolTip;
    private ContextMenu? navigationChoices;
    private ContextMenu? sourceContextMenu;
    private int? contextOffset;
    private bool contextFromMouse;
    private readonly List<NavigationPoint> navigationBack = [];
    private readonly List<NavigationPoint> navigationForward = [];
    private sealed record NavigationPoint(string Path, TextAnchor Anchor, CodePosition Position, double Vertical, double Horizontal);
    private bool CanNavigateCode => !closing && activeDocument is not null && WorkspaceTabs.SelectedItem == EditorTab && CodeIntelligenceService.Supports(activeDocument.RelativePath);

    private CodeDocumentSnapshot[] CaptureCodeDocuments() => editorDocuments.Where(session => CodeIntelligenceService.Supports(session.Source.RelativePath))
        .Select(session => new CodeDocumentSnapshot(session.Source.RelativePath, session.Buffer.Text)).ToArray();

    private void InitializeCodeNavigation()
    {
        SourceEditor.MouseHover += (_, e) =>
        {
            if (sourceContextMenu?.IsOpen == true || completionWindow is not null || signatureWindow is not null) return;
            if (TryShowDiagnosticHover(e.GetPosition(SourceEditor))) return;
            if (HitSymbol(e.GetPosition(SourceEditor)) is { } offset) QueueSymbolHover(offset, e.GetPosition(SourceEditor));
        };
        SourceEditor.MouseHoverStopped += (_, _) => HideSymbolHover();
        SourceEditor.MouseLeave += (_, _) => HideSymbolHover();
        SourceEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => HideSymbolHover();
        SourceEditor.TextArea.Caret.PositionChanged += (_, _) => CancelSymbolRequests();
        SourceEditor.PreviewMouseDown += (_, _) => HideSymbolHover();
        SourceEditor.PreviewMouseRightButtonDown += (_, e) =>
        {
            contextFromMouse = true; contextOffset = HitSymbol(e.GetPosition(SourceEditor));
            if (contextOffset is { } offset)
            {
                if (offset < SourceEditor.SelectionStart || offset >= SourceEditor.SelectionStart + SourceEditor.SelectionLength) SourceEditor.CaretOffset = offset;
                SourceEditor.Focus();
            }
        };
        Deactivated += (_, _) => HideSymbolHover();
        var menu = new ContextMenu(); SetMenuColors(menu);
        sourceContextMenu = menu;
        var definition = new MenuItem { Header = "转到定义", InputGestureText = "F12" };
        var declaration = new MenuItem { Header = "转到声明", InputGestureText = "Ctrl+F12" };
        var back = new MenuItem { Header = "返回上一个位置", InputGestureText = "Alt+←" };
        var forward = new MenuItem { Header = "前进到下一个位置", InputGestureText = "Alt+→" };
        definition.Click += (_, _) => QueueCodeNavigation(declaration: false, contextOffset);
        declaration.Click += (_, _) => QueueCodeNavigation(declaration: true, contextOffset);
        back.Click += async (_, _) => await RunAsync(_ => TravelNavigationAsync(backwards: true));
        forward.Click += async (_, _) => await RunAsync(_ => TravelNavigationAsync(backwards: false));
        menu.Items.Add(definition); menu.Items.Add(declaration); menu.Items.Add(new Separator());
        menu.Items.Add(back); menu.Items.Add(forward); menu.Items.Add(new Separator());
        foreach (var (label, command) in new[] { ("撤销", ApplicationCommands.Undo), ("重做", ApplicationCommands.Redo), ("剪切", ApplicationCommands.Cut), ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = label, Command = command, CommandTarget = SourceEditor.TextArea });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "切换行注释", InputGestureText = "Ctrl+/", Command = SourceCommands.ToggleComment, CommandTarget = this });
        menu.Opened += (_, _) =>
        {
            HideSymbolHover();
            if (!contextFromMouse) contextOffset = SourceEditor.CaretOffset;
            definition.IsEnabled = declaration.IsEnabled = CanNavigateCode && services.Intelligence.IsReady && contextOffset is { } offset && IsSymbolContext(offset);
            back.IsEnabled = navigationBack.Count > 0; forward.IsEnabled = navigationForward.Count > 0;
        };
        menu.Closed += (_, _) => contextFromMouse = false;
        SourceEditor.ContextMenu = menu; SourceEditor.TextArea.ContextMenu = menu;
    }

    private int? HitSymbol(Point point)
    {
        if (!CanNavigateCode) return null;
        var view = SourceEditor.TextArea.TextView;
        var local = SourceEditor.TranslatePoint(point, view);
        if (local.X < 0 || local.Y < 0 || local.X >= view.ActualWidth || local.Y >= view.ActualHeight) return null;
        var position = view.GetPositionFloor(local + view.ScrollOffset);
        if (position is null) return null;
        var line = SourceEditor.Document.GetLineByNumber(position.Value.Line);
        if (position.Value.Column > line.Length) return null;
        var offset = SourceEditor.Document.GetOffset(position.Value.Location);
        return offset < SourceEditor.Document.TextLength && IsIdentifier(SourceEditor.Document.GetCharAt(offset)) && IsSymbolContext(offset) ? offset : null;
    }
    private static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';
    private bool IsSymbolContext(int offset)
    {
        if (offset < 0 || offset > SourceEditor.Document.TextLength) return false;
        if (offset == SourceEditor.Document.TextLength || !IsIdentifier(SourceEditor.Document.GetCharAt(offset))) offset--;
        if (offset < 0 || !IsIdentifier(SourceEditor.Document.GetCharAt(offset))) return false;
        var line = SourceEditor.Document.GetLineByOffset(offset);
        if (SourceEditor.Document.GetText(line).TrimStart().StartsWith("#include", StringComparison.Ordinal)) return true;
        if (SourceEditor.TextArea.GetService(typeof(IHighlighter)) is not IHighlighter highlighter) return true;
        return !highlighter.HighlightLine(line.LineNumber).Sections.Any(section => section.Offset <= offset && section.Offset + section.Length > offset && section.Color.Name is "Comment" or "String");
    }

    private void HideSymbolHover()
    {
        hoverCancellation?.Cancel();
        if (symbolToolTip is not null) { symbolToolTip.IsOpen = false; symbolToolTip = null; }
    }
    private void CancelSymbolRequests()
    {
        HideSymbolHover(); navigationCancellation?.Cancel();
        if (navigationChoices is not null) { navigationChoices.IsOpen = false; navigationChoices = null; }
    }
    private void QueueSymbolHover(int offset, Point point)
    {
        HideSymbolHover();
        if (!CanNavigateCode || !services.Intelligence.IsReady || !IsSymbolContext(offset)) return;
        var cancellation = new CancellationTokenSource(); hoverCancellation = cancellation;
        var document = SourceEditor.Document; var text = document.Text; var path = activeDocument!.RelativePath;
        var documents = CaptureCodeDocuments();
        hoverTask = FetchAsync();
        async Task FetchAsync()
        {
            try
            {
                var result = await services.Intelligence.HoverAsync(path, text, offset, cancellation.Token, documents);
                if (cancellation.IsCancellationRequested || SourceEditor.Document != document || document.Text != text || !CanNavigateCode || result is null) return;
                ShowSymbolHover(result, point);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) { Status.Text = "悬停信息：" + ex.Message; Log(ex.ToString()); } }
            finally { if (hoverCancellation == cancellation) hoverCancellation = null; cancellation.Dispose(); }
        }
    }
    private void ShowSymbolHover(CodeHover hover, Point point)
    {
        var body = new StackPanel { MaxWidth = 620, Margin = new Thickness(9, 6, 9, 6) };
        if (hover.Contents.Length > 0)
        {
            var content = hover.Contents.Length > 4000 ? hover.Contents[..4000] + "\n…" : hover.Contents;
            body.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxHeight = 230, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 });
        }
        foreach (var location in hover.Declarations.Take(4))
        {
            var label = new TextBlock { Text = $"声明位置：{location.DisplayPath}:{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); body.Children.Add(label);
        }
        if (hover.Declarations.Count == 0) body.Children.Add(new TextBlock { Text = "语言服务未返回可跳转的声明位置。", Margin = new Thickness(0, 8, 0, 0) });
        var help = new TextBlock { Text = "右键 → 转到声明 / 定义     F12 转到定义", FontSize = 11, Margin = new Thickness(0, 9, 0, 0) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(help);
        var tip = new ToolTip { Content = body, PlacementTarget = SourceEditor, Placement = PlacementMode.Relative, HorizontalOffset = point.X + 12, VerticalOffset = point.Y + 24, IsHitTestVisible = false, StaysOpen = true };
        tip.SetResourceReference(Control.BackgroundProperty, "Surface"); tip.SetResourceReference(Control.ForegroundProperty, "Text"); tip.SetResourceReference(Control.BorderBrushProperty, "Border");
        symbolToolTip = tip; tip.IsOpen = true;
    }

    private void QueueCodeNavigation(bool declaration, int? at = null)
    {
        CloseCodeAssistance();
        var offset = at ?? SourceEditor.CaretOffset;
        if (!CanNavigateCode || !IsSymbolContext(offset)) return;
        if (!services.Intelligence.IsReady) { Status.Text = "跳转服务尚未就绪。"; return; }
        var cancellation = new CancellationTokenSource(); navigationCancellation = cancellation;
        var document = SourceEditor.Document; var text = document.Text; var path = activeDocument!.RelativePath;
        var documents = CaptureCodeDocuments();
        navigationTask = FetchAsync();
        async Task FetchAsync()
        {
            try
            {
                var locations = await services.Intelligence.NavigateAsync(path, text, offset, declaration, cancellation.Token, documents);
                if (cancellation.IsCancellationRequested || SourceEditor.Document != document || document.Text != text || !CanNavigateCode) return;
                if (locations.Count == 0) { Status.Text = declaration ? "未找到可跳转的声明。" : "未找到可跳转的定义。"; return; }
                if (locations.Count == 1) await JumpToCodeAsync(locations[0], cancellation.Token);
                else
                {
                    var choices = new ContextMenu { PlacementTarget = SourceEditor, Placement = PlacementMode.MousePoint };
                    SetMenuColors(choices);
                    foreach (var location in locations)
                    {
                        var item = new MenuItem { Header = $"{location.DisplayPath}:{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}" };
                        item.Click += async (_, _) => await RunAsync(token => JumpToCodeAsync(location, token)); choices.Items.Add(item);
                    }
                    navigationChoices = choices; choices.IsOpen = true;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested) { Status.Text = "代码跳转：" + ex.Message; Log(ex.ToString()); } }
            finally { if (navigationCancellation == cancellation) navigationCancellation = null; cancellation.Dispose(); }
        }
    }

    private NavigationPoint? CaptureNavigationPoint()
    {
        if (activeDocument is null || WorkspaceTabs.SelectedItem != EditorTab) return null;
        var anchor = SourceEditor.Document.CreateAnchor(SourceEditor.CaretOffset); anchor.SurviveDeletion = true;
        return new(activeDocument.RelativePath, anchor, CodePositions.FromOffset(SourceEditor.Text, SourceEditor.CaretOffset), SourceEditor.VerticalOffset, SourceEditor.HorizontalOffset);
    }
    private static void PushNavigation(List<NavigationPoint> list, NavigationPoint point)
    {
        list.Add(point); if (list.Count > 100) list.RemoveAt(0);
    }
    private async Task JumpToCodeAsync(CodeLocation location, CancellationToken token)
    {
        var existing = FindEditor(location.DocumentPath);
        var source = existing?.Source ?? await services.Intelligence.ReadNavigationDocumentAsync(location, token);
        token.ThrowIfCancellationRequested();
        var text = existing?.Buffer.Text ?? source.Text;
        var start = CodePositions.ToOffset(text, location.Range.Start);
        var end = CodePositions.ToOffset(text, location.Range.End);
        var origin = CaptureNavigationPoint();
        if (existing is null) ShowSource(source); else ShowDocument(existing.Tab);
        editorViewRevision++;
        SourceEditor.Select(start, Math.Max(0, end - start)); SourceEditor.CaretOffset = start;
        SourceEditor.UpdateLayout(); SourceEditor.ScrollTo(location.Range.Start.Line + 1, location.Range.Start.Character + 1); SourceEditor.Focus();
        if (origin is not null) { PushNavigation(navigationBack, origin); navigationForward.Clear(); }
        Status.Text = $"已定位 {location.DisplayPath}:{location.Range.Start.Line + 1} · Alt+← 返回";
    }
    private async Task TravelNavigationAsync(bool backwards)
    {
        var from = backwards ? navigationBack : navigationForward;
        var to = backwards ? navigationForward : navigationBack;
        if (from.Count == 0) return;
        var point = from[^1]; var origin = CaptureNavigationPoint();
        var existing = FindEditor(point.Path);
        var source = existing?.Source ?? await services.Intelligence.ReadNavigationDocumentAsync(new(point.Path, new(point.Position, point.Position), point.Path));
        var buffer = existing?.Buffer ?? new TextDocument(source.Text);
        var line = buffer.GetLineByNumber(Math.Clamp(point.Position.Line + 1, 1, buffer.LineCount));
        var offset = existing is not null && point.Anchor.Document == existing.Buffer && !point.Anchor.IsDeleted ? point.Anchor.Offset :
            line.Offset + Math.Clamp(point.Position.Character, 0, line.Length);
        if (existing is null) ShowSource(source); else ShowDocument(existing.Tab);
        editorViewRevision++; SourceEditor.Select(offset, 0); SourceEditor.CaretOffset = offset; SourceEditor.UpdateLayout();
        SourceEditor.ScrollToVerticalOffset(point.Vertical); SourceEditor.ScrollToHorizontalOffset(point.Horizontal); SourceEditor.Focus();
        from.RemoveAt(from.Count - 1); if (origin is not null) PushNavigation(to, origin);
    }
    private static void SetMenuColors(ContextMenu menu)
    {
        menu.SetResourceReference(StyleProperty, "CodeContextMenu");
        menu.SetResourceReference(Control.BackgroundProperty, "Surface"); menu.SetResourceReference(Control.ForegroundProperty, "Text"); menu.SetResourceReference(Control.BorderBrushProperty, "Border");
    }
}

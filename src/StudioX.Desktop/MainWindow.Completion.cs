namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private CompletionWindow? completionWindow;
    private InsightWindow? signatureWindow;
    private CancellationTokenSource? assistCancellation;
    private Task assistTask = Task.CompletedTask;
    private long assistRevision;
    private bool renderingAssistancePreview;

    private void InitializeCodeAssistance()
    {
        SourceEditor.TextArea.TextEntered += Code_TextEntered;
        SourceEditor.TextArea.TextEntering += (_, e) =>
        {
            if (e.Text.Any(c => !char.IsLetterOrDigit(c) && c != '_')) completionWindow?.Close();
        };
        SourceEditor.TextArea.PreviewKeyDown += Code_PreviewKeyDown;
        SourceEditor.TextArea.Caret.PositionChanged += (_, _) => CancelCodeRequest();
        SourceEditor.LostKeyboardFocus += (_, _) => CancelCodeRequest();
        SourceEditor.IsVisibleChanged += (_, _) => { if (!SourceEditor.IsVisible) CloseCodeAssistance(); };
    }
    private void CancelCodeRequest() { assistRevision++; assistCancellation?.Cancel(); }
    private void CloseCodeAssistance()
    {
        CancelSymbolRequests();
        CancelCodeRequest(); completionWindow?.Close(); signatureWindow?.Close();
    }
    private bool IsCMakeDocument => activeDocument is not null && CMakeAssistanceService.Supports(activeDocument.RelativePath);
    private bool CanAssist => !closing && !SourceEditor.IsReadOnly && activeDocument is not null &&
        (IsCMakeDocument || CodeIntelligenceService.Supports(activeDocument.RelativePath)) && WorkspaceTabs.SelectedItem == EditorTab;
    private bool IsCodeContext()
    {
        // CMake 引号内仍需补全变量和路径，由语言服务判断注释与括号字符串。
        if (IsCMakeDocument) return true;
        if (SourceEditor.CaretOffset == 0) return true;
        var line = SourceEditor.Document.GetLineByOffset(SourceEditor.CaretOffset);
        var prefix = SourceEditor.Document.GetText(line.Offset, SourceEditor.CaretOffset - line.Offset).TrimStart();
        if (prefix.StartsWith("#include", StringComparison.Ordinal)) return true;
        if (SourceEditor.TextArea.GetService(typeof(IHighlighter)) is not IHighlighter highlighter) return true;
        var previous = SourceEditor.CaretOffset - 1;
        return !highlighter.HighlightLine(line.LineNumber).Sections.Any(section => section.Offset <= previous && section.Offset + section.Length > previous && section.Color.Name is "Comment" or "String");
    }
    private void Code_TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (!CanAssist || e.Text.Length != 1 || !IsCodeContext()) return;
        var character = e.Text[0];
        if (IsCMakeDocument)
        {
            if (character == ')') { CloseCodeAssistance(); return; }
            if (char.IsLetterOrDigit(character) || character is '_' or '.' or '/' or '$' or '{' or '"' or '(' || char.IsWhiteSpace(character))
                QueueAssistance(signature: false);
            else CloseCodeAssistance();
            return;
        }
        if (character is '(' or ',' or ')') { QueueAssistance(signature: true); return; }
        if (char.IsLetter(character) || character == '_' || character is '.' or '>' or ':' or '"' or '<' or '/' || char.IsDigit(character) && completionWindow is not null)
            QueueAssistance(signature: false);
        else signatureWindow?.Close();
    }
    private void Code_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemQuestion or Key.Divide)
        { ToggleSourceComment(); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z && CanEditSource)
        { SourceEditor.Redo(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { CloseCodeAssistance(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Space)
        { e.Handled = true; QueueAssistance(signature: false, manual: true); }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Space)
        { e.Handled = true; QueueAssistance(signature: true, manual: true); }
    }
    private void CompleteCode_Click(object sender, RoutedEventArgs e) { SourceEditor.Focus(); QueueAssistance(signature: false, manual: true); }
    private void ParameterInfo_Click(object sender, RoutedEventArgs e) { SourceEditor.Focus(); QueueAssistance(signature: true, manual: true); }
    private void QueueAssistance(bool signature, bool manual = false)
    {
        CancelCodeRequest();
        if (!CanAssist || !IsCodeContext()) return;
        var cmake = IsCMakeDocument;
        if (!cmake && !services.Intelligence.IsReady) { if (manual) Status.Text = "代码提示服务未就绪，可重新打开工程重试。"; return; }
        var cancellation = new CancellationTokenSource(); assistCancellation = cancellation;
        var revision = assistRevision;
        var document = SourceEditor.Document; var text = document.Text; var offset = SourceEditor.CaretOffset;
        var path = activeDocument!.RelativePath;
        var documents = CaptureCodeDocuments();
        assistTask = RequestAssistanceAsync();
        async Task RequestAssistanceAsync()
        {
            try
            {
                if (!manual) await Task.Delay(160, cancellation.Token);
                bool Current() => !cancellation.IsCancellationRequested && revision == assistRevision && CanAssist &&
                    SourceEditor.Document == document && SourceEditor.CaretOffset == offset && document.Text == text;
                if (cmake)
                {
                    var result = await services.CMake.GetAsync(projectDirectory, path, text, offset, cancellation.Token);
                    if (!Current()) return;
                    completionWindow?.Close(); signatureWindow?.Close();
                    if (!signature && result.Suggestions.Count > 0) ShowCompletions(result.Suggestions, text, offset, result.Signature);
                    else if (result.Signature is not null) ShowSignature(result.Signature);
                    else if (manual) Status.Text = "当前位置没有 CMake 提示。";
                    return;
                }
                if (signature)
                {
                    var result = await services.Intelligence.SignatureAsync(path, text, offset, cancellation.Token, documents);
                    if (!Current()) return;
                    signatureWindow?.Close();
                    if (result is not null) ShowSignature(result);
                    else if (manual) Status.Text = "当前位置没有可显示的函数参数。";
                }
                else
                {
                    var results = await services.Intelligence.CompleteAsync(path, text, offset, cancellation.Token, documents);
                    if (!Current()) return;
                    ShowCompletions(results, text, offset);
                    if (manual && results.Count == 0) Status.Text = "当前位置没有匹配的代码提示。";
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (revision == assistRevision) { Status.Text = "代码提示：" + ex.Message; Log(ex.ToString()); }
            }
            finally
            {
                foreach (var line in services.Intelligence.DrainLog()) Log("clangd：" + line);
                if (assistCancellation == cancellation) assistCancellation = null;
                cancellation.Dispose();
            }
        }
    }
    private void ShowCompletions(IReadOnlyList<CodeSuggestion> suggestions, string snapshot, int offset, CodeSignature? context = null)
    {
        completionWindow?.Close(); signatureWindow?.Close();
        if (suggestions.Count == 0) return;
        var prefixStart = offset;
        while (prefixStart > 0 && (char.IsLetterOrDigit(snapshot[prefixStart - 1]) || snapshot[prefixStart - 1] == '_')) prefixStart--;
        var window = new CompletionWindow(SourceEditor.TextArea) { Width = 610, MaxHeight = context is null ? 300 : 390, FontSize = 13, StartOffset = prefixStart, EndOffset = offset, CloseWhenCaretAtBeginning = false };
        SetAssistColors(window);
        var body = new DockPanel();
        var help = new TextBlock { Text = "↑↓ 选择     Tab / Enter 插入     Esc 关闭" + (IsCMakeDocument ? "     Ctrl+Shift+Space 用法" : ""), FontSize = 11, Margin = new Thickness(10, 6, 10, 6) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(help, Dock.Bottom); body.Children.Add(help);
        if (context is not null)
        {
            var hint = new StackPanel { Margin = new Thickness(10, 6, 10, 5) };
            hint.Children.Add(new TextBlock { Text = context.Label, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (context.Parameters.Count > 0)
            {
                var parameter = new TextBlock { Text = context.Parameters[Math.Clamp(context.ActiveParameter, 0, context.Parameters.Count - 1)], FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
                parameter.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); hint.Children.Add(parameter);
            }
            DockPanel.SetDock(hint, Dock.Bottom); body.Children.Add(hint);
        }
        window.CompletionList.MaxHeight = 245;
        window.Content = null; body.Children.Add(window.CompletionList); window.Content = body;
        var priority = suggestions.Count;
        foreach (var suggestion in suggestions)
        {
            var start = prefixStart; var end = offset;
            if (suggestion.Range is { } range)
            {
                try { start = CodePositions.ToOffset(snapshot, range.Start); end = CodePositions.ToOffset(snapshot, range.End); }
                catch (ArgumentOutOfRangeException) { continue; }
                if (start > offset || end < offset || range.Start.Line != range.End.Line) continue;
            }
            if (window.CompletionList.CompletionData.Count == 0) window.StartOffset = start;
            if (start != window.StartOffset) continue;
            if (suggestion.InsertText.All(c => char.IsLetterOrDigit(c) || c == '_'))
                while (end < snapshot.Length && (char.IsLetterOrDigit(snapshot[end]) || snapshot[end] == '_')) end++;
            window.CompletionList.CompletionData.Add(new CodeCompletionData(SourceEditor.Document, suggestion, start, end, priority--,
                showSignature =>
                {
                    if (!showSignature) return;
                    var insertedRevision = assistRevision;
                    _ = Dispatcher.BeginInvoke(() => { if (insertedRevision == assistRevision) QueueAssistance(signature: !IsCMakeDocument); }, DispatcherPriority.Input);
                }));
        }
        if (window.CompletionList.CompletionData.Count == 0) { window.Close(); return; }
        completionWindow = window;
        window.Closed += (_, _) => { if (completionWindow == window) completionWindow = null; };
        window.Show();
        if (renderingAssistancePreview) window.Left = -20000;
        window.CompletionList.ApplyTemplate();
        if (window.CompletionList.ListBox is { } list)
        {
            list.SetResourceReference(Control.BackgroundProperty, "Surface"); list.SetResourceReference(Control.ForegroundProperty, "Text");
            list.ItemContainerStyle = (Style)FindResource("CodeCompletionItemStyle");
        }
        window.CompletionList.SelectItem(snapshot[window.StartOffset..offset]);
    }
    private void ShowSignature(CodeSignature signature)
    {
        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 9), MaxWidth = 660 };
        body.Children.Add(new TextBlock { Text = signature.Label, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap });
        if (signature.Parameters.Count > 0)
        {
            var parameter = Math.Clamp(signature.ActiveParameter, 0, signature.Parameters.Count - 1);
            var current = new TextBlock { Text = (IsCMakeDocument ? "当前位置  ·  " : $"参数 {parameter + 1}/{signature.Parameters.Count}  ·  ") + signature.Parameters[parameter], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            current.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); body.Children.Add(current);
        }
        if (signature.Documentation.Length > 0) body.Children.Add(new TextBlock { Text = signature.Documentation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var window = new InsightWindow(SourceEditor.TextArea) { Content = body, MaxWidth = 700 };
        SetAssistColors(window); signatureWindow = window;
        window.Closed += (_, _) => { if (signatureWindow == window) signatureWindow = null; }; window.Show();
        if (renderingAssistancePreview) window.Left = -20000;
    }
    private static void SetAssistColors(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "Surface"); window.SetResourceReference(Control.ForegroundProperty, "Text");
        window.SetResourceReference(Control.BorderBrushProperty, "Border"); window.BorderThickness = new Thickness(1);
    }
}

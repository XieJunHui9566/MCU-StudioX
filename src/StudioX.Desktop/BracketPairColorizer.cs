namespace StudioX.Desktop;

using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Application.CodeIntelligence;

/// <summary>语法着色之后覆盖括号字形颜色，柔光层直接复用相同的排版结果。</summary>
internal sealed class BracketPairColorizer : DocumentColorizingTransformer, IDisposable
{
    private static readonly Brush[] Dark = Palette("#F2CB6F", "#BB9AF7", "#67D8E5", "#F59CC5", "#9CD784", "#88B7FF");
    private static readonly Brush[] Light = Palette("#936000", "#793DB5", "#007F8A", "#AD356F", "#417719", "#285CB4");
    private readonly TextEditor editor;
    private readonly Action<Exception> onError;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? cancellation;
    private string language = "Text";
    private Brush[] palette = Dark;
    private long revision;
    private bool disposed;
    internal ColoredBracket[] Brackets { get; private set; } = [];

    public BracketPairColorizer(TextEditor editor, Action<Exception> onError)
    {
        this.editor = editor; this.onError = onError;
        editor.TextChanged += TextChanged;
        timer.Tick += async (_, _) => await RefreshAsync();
    }
    public void Configure(string nextLanguage, bool dark)
    {
        palette = dark ? Dark : Light;
        var transformers = editor.TextArea.TextView.LineTransformers;
        transformers.Remove(this); transformers.Add(this);
        if (language != nextLanguage) { language = nextLanguage; QueueRefresh(); }
        editor.TextArea.TextView.Redraw();
    }
    private void TextChanged(object? sender, EventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        revision++; cancellation?.Cancel(); timer.Stop(); Brackets = [];
        editor.TextArea.TextView.Redraw();
        if (!disposed && BracketPairs.Supports(language)) timer.Start();
    }
    internal async Task RefreshAsync()
    {
        timer.Stop(); cancellation?.Cancel();
        if (disposed || !BracketPairs.Supports(language)) return;
        var request = new CancellationTokenSource(); cancellation = request;
        var version = ++revision; var document = editor.Document;
        var text = document.Text; var syntax = language;
        try
        {
            var result = await Task.Run(() => BracketPairs.Find(text, syntax, request.Token), request.Token);
            if (disposed || request.IsCancellationRequested || revision != version || editor.Document != document) return;
            Brackets = result; editor.TextArea.TextView.Redraw();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!disposed && revision == version) onError(ex); }
        finally { if (cancellation == request) cancellation = null; request.Dispose(); }
    }
    protected override void ColorizeLine(DocumentLine line)
    {
        // 仅处理可见行中的括号，滚动/字号变化不重新扫描全文。
        var low = 0; var high = Brackets.Length;
        while (low < high) { var mid = low + (high - low) / 2; if (Brackets[mid].Offset < line.Offset) low = mid + 1; else high = mid; }
        for (var i = low; i < Brackets.Length && Brackets[i].Offset < line.EndOffset; i++)
        {
            var bracket = Brackets[i];
            ChangeLinePart(bracket.Offset, bracket.Offset + 1, element => element.TextRunProperties.SetForegroundBrush(palette[bracket.Depth % palette.Length]));
        }
    }
    private static Brush[] Palette(params string[] colors) => colors.Select(color =>
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return (Brush)brush; }).ToArray();
    public void Dispose()
    {
        disposed = true; revision++; timer.Stop(); cancellation?.Cancel();
        editor.TextChanged -= TextChanged;
    }
}

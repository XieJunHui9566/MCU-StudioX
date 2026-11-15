namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using StudioX.Application.CodeIntelligence;

/// <summary>折叠状态属于文件；文档切换前卸载，后台扫描不占用输入线程。</summary>
internal sealed class EditorFoldingController : IDisposable
{
    private readonly TextEditor editor;
    private readonly Action<Exception> onError;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? cancellation;
    private EditorDocumentSession? session;
    private FoldingManager? manager;
    private bool restoring;
    private bool disposed;
    internal FoldingManager? Manager => manager;

    public EditorFoldingController(TextEditor editor, Action<Exception> onError)
    {
        this.editor = editor;
        this.onError = onError;
        editor.TextChanged += Changed;
        timer.Tick += async (_, _) => await RefreshAsync();
        FoldingMargin.SetFoldingMarkerBrush(editor.TextArea, Resource(editor, "Muted"));
        FoldingMargin.SetFoldingMarkerBackgroundBrush(editor.TextArea, Resource(editor, "EditorSurface"));
        editor.TextArea.SetResourceReference(FoldingMargin.FoldingMarkerBrushProperty, "Muted");
        editor.TextArea.SetResourceReference(FoldingMargin.FoldingMarkerBackgroundBrushProperty, "EditorSurface");
        editor.TextArea.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBrushProperty, "Accent");
        editor.TextArea.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, "Panel");
    }

    private static Brush Resource(FrameworkElement editor, string name) => editor.TryFindResource(name) as Brush ?? Brushes.Gray;

    public void SetDocument(EditorDocumentSession? next)
    {
        if (session == next && editor.Document == next?.Buffer)
        {
            return;
        }
        timer.Stop();
        cancellation?.Cancel();
        SaveState();
        if (manager is not null)
        {
            FoldingManager.Uninstall(manager);
            manager = null;
        }
        session = next;
        editor.Document = next?.Buffer ?? new TextDocument();
        if (next is not null && CodeFolding.Supports(CodeLanguage.ForFile(next.Source.RelativePath)))
        {
            manager = FoldingManager.Install(editor.TextArea);
            ApplyTheme();
            restoring = true;
            Changed(null, EventArgs.Empty);
        }
    }

    private void Changed(object? sender, EventArgs e)
    {
        cancellation?.Cancel();
        timer.Stop();
        if (!disposed && manager is not null)
        {
            timer.Start();
        }
    }

    internal void ApplyTheme()
    {
        foreach (var margin in editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.SetResourceReference(FoldingMargin.FoldingMarkerBrushProperty, "Muted");
            margin.SetResourceReference(FoldingMargin.FoldingMarkerBackgroundBrushProperty, "EditorSurface");
            margin.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBrushProperty, "Accent");
            margin.SetResourceReference(FoldingMargin.SelectedFoldingMarkerBackgroundBrushProperty, "Panel");
        }
        FoldingElementGenerator.TextBrush = Resource(editor, "Muted");
        editor.TextArea.TextView.Redraw();
    }

    internal async Task RefreshAsync()
    {
        timer.Stop();
        cancellation?.Cancel();
        if (disposed || manager is null || session is null)
        {
            return;
        }
        var request = new CancellationTokenSource();
        cancellation = request;
        var target = session;
        var document = editor.Document;
        var version = document.Version;
        var text = document.Text;
        try
        {
            var ranges = await Task.Run(() => CodeFolding.Find(text, CodeLanguage.ForFile(target.Source.RelativePath), request.Token), request.Token);
            if (disposed || request.IsCancellationRequested || session != target || editor.Document != document || document.Version != version)
            {
                return;
            }
            manager.UpdateFoldings(ranges.Select(range => new NewFolding(range.StartOffset, range.EndOffset) { Name = " … " }), -1);
            if (restoring)
            {
                var collapsed = target.CollapsedFoldings.Where(pair => !pair.Start.IsDeleted && !pair.End.IsDeleted)
                    .Select(pair => (pair.Start.Offset, pair.End.Offset)).ToHashSet();
                foreach (var folding in manager.AllFoldings)
                {
                    folding.IsFolded = collapsed.Contains((folding.StartOffset, folding.EndOffset));
                }
                restoring = false;
            }
            FoldingElementGenerator.TextBrush = Resource(editor, "Muted");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!disposed && !request.IsCancellationRequested)
            {
                onError(ex);
            }
        }
        finally
        {
            if (cancellation == request)
            {
                cancellation = null;
            }
            request.Dispose();
        }
    }

    private void SaveState()
    {
        if (session is null || manager is null || restoring)
        {
            return;
        }
        session.CollapsedFoldings.Clear();
        foreach (var folding in manager.AllFoldings.Where(folding => folding.IsFolded))
        {
            var start = session.Buffer.CreateAnchor(folding.StartOffset);
            var end = session.Buffer.CreateAnchor(folding.EndOffset);
            start.MovementType = AnchorMovementType.AfterInsertion;
            end.MovementType = AnchorMovementType.BeforeInsertion;
            session.CollapsedFoldings.Add((start, end));
        }
    }

    public async Task SetFoldedAsync(bool folded, bool all)
    {
        var target = session;
        var targetManager = manager;
        await RefreshAsync();
        if (manager is null || manager != targetManager || session != target || disposed)
        {
            return;
        }
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        var selected = all ? manager.AllFoldings.ToArray() : manager.AllFoldings
            .Where(folding => folding.StartOffset <= editor.CaretOffset && folding.EndOffset >= editor.CaretOffset ||
                folding.StartOffset >= line.Offset && folding.StartOffset <= (line.NextLine?.EndOffset ?? line.EndOffset))
            .OrderBy(folding => folding.Length).Take(1).ToArray();
        if (folded && selected.Length > 0)
        {
            editor.CaretOffset = selected.Min(folding => folding.StartOffset);
        }
        foreach (var folding in selected)
        {
            folding.IsFolded = folded;
        }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        cancellation?.Cancel();
        SaveState();
        editor.TextChanged -= Changed;
        if (manager is not null)
        {
            FoldingManager.Uninstall(manager);
            manager = null;
        }
    }
}

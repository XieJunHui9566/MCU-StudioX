namespace StudioX.Application.CodeIntelligence;

using System.Collections.Concurrent;
using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private readonly ConcurrentDictionary<string, (int Version, string Text)> diagnosticDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CodeDiagnosticBatch> diagnosticBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> invalidatedDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private int languageGeneration;

    public IReadOnlyList<CodeDiagnosticBatch> GetDiagnostics() => diagnosticBatches.Values.ToArray();

    private int TrackDiagnosticText(string uri, string text)
    {
        var next = ++version;
        diagnosticDocuments[uri] = (next, text);
        invalidatedDiagnostics.TryRemove(uri, out _);
        diagnosticBatches.TryRemove(uri, out _);
        return next;
    }

    private void InvalidateDependentDiagnostics(string changedUri)
    {
        foreach (var uri in diagnosticDocuments.Keys.Where(uri => !uri.Equals(changedUri, StringComparison.OrdinalIgnoreCase)))
        {
            invalidatedDiagnostics[uri] = 0;
            diagnosticBatches.TryRemove(uri, out _);
        }
    }

    private void CloseDiagnosticDocument(string uri)
    {
        diagnosticDocuments.TryRemove(uri, out _);
        diagnosticBatches.TryRemove(uri, out _);
        invalidatedDiagnostics.TryRemove(uri, out _);
    }

    private void ReceiveDiagnostics(int generation, string root, string method, JsonElement parameters)
    {
        if (method != "textDocument/publishDiagnostics" || generation != Volatile.Read(ref languageGeneration))
        {
            return;
        }
        try
        {
            var uri = parameters.GetProperty("uri").GetString()!;
            // 请求 versionSupport；无版本诊断无法证明属于当前快照，明确忽略。
            if (!parameters.TryGetProperty("version", out var value) || !value.TryGetInt32(out var publishedVersion) ||
                !diagnosticDocuments.TryGetValue(uri, out var expected) || expected.Version != publishedVersion || invalidatedDiagnostics.ContainsKey(uri))
            {
                return;
            }
            var path = new Uri(uri).LocalPath;
            if (!IsInside(root, path))
            {
                return;
            }
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            _ = PathBoundary.Resolve(root, relative);
            var items = new List<CodeDiagnostic>();
            foreach (var item in parameters.GetProperty("diagnostics").EnumerateArray().Take(1000))
            {
                var range = JsonSerializer.Deserialize<CodeRange>(item.GetProperty("range"), JsonStore.Options)!;
                var start = CodePositions.ToOffset(expected.Text, range.Start);
                var end = CodePositions.ToOffset(expected.Text, range.End);
                if (end < start)
                {
                    continue;
                }
                items.Add(new(range, item.TryGetProperty("severity", out var severity) ? severity.GetInt32() : 1,
                    String(item, "message"), String(item, "source", "clangd"), item.TryGetProperty("code", out var code) ? code.ToString() : "", item.Clone()));
            }
            if (generation == Volatile.Read(ref languageGeneration) && diagnosticDocuments.TryGetValue(uri, out var current) && current.Version == publishedVersion && !invalidatedDiagnostics.ContainsKey(uri))
            {
                diagnosticBatches[uri] = new(root, relative, expected.Text, publishedVersion, items);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or StudioXException)
        {
            log.Enqueue("无法读取 clangd 诊断：" + ex);
        }
    }

    public async Task SynchronizeDiagnosticsAsync(string path, string text, IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsReady || !Supports(path))
            {
                return;
            }
            await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            // 当前文档最后同步后，前面的引用方也要刷新；重开未变文本只更新自己的诊断。
            foreach (var document in documents.Where(d => Supports(d.Path)))
            {
                await SynchronizeAsync(document.Path, document.Text, token).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }
}

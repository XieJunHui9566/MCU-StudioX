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
    private readonly object diagnosticStateLock = new();
    private int diagnosticsSuspended, diagnosticRefreshRequired;

    public bool DiagnosticsSuspended => Volatile.Read(ref diagnosticsSuspended) != 0;

    public IReadOnlyList<CodeDiagnosticBatch> GetDiagnostics()
    {
        lock (diagnosticStateLock) return DiagnosticsSuspended ? [] : diagnosticBatches.Values.ToArray();
    }

    /// <summary>调试期间暂停实时诊断收集；保留语言导航，恢复时重新解析，避免旧消息重新出现。</summary>
    public void SetDiagnosticsSuspended(bool suspended)
    {
        lock (diagnosticStateLock)
        {
            if (DiagnosticsSuspended == suspended) return;
            Volatile.Write(ref diagnosticsSuspended, suspended ? 1 : 0);
            diagnosticBatches.Clear();
            foreach (var uri in diagnosticDocuments.Keys) invalidatedDiagnostics[uri] = 0;
            Interlocked.Exchange(ref diagnosticRefreshRequired, 1);
        }
    }

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
        if (DiagnosticsSuspended || method != "textDocument/publishDiagnostics" || generation != Volatile.Read(ref languageGeneration))
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
            lock (diagnosticStateLock)
            {
                if (!DiagnosticsSuspended && generation == Volatile.Read(ref languageGeneration) && diagnosticDocuments.TryGetValue(uri, out var current) && current.Version == publishedVersion && !invalidatedDiagnostics.ContainsKey(uri))
                {
                    diagnosticBatches[uri] = new(root, relative, expected.Text, publishedVersion, items);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or StudioXException)
        {
            log.Enqueue("无法读取 clangd 诊断：" + ex);
        }
    }

    public async Task SynchronizeDiagnosticsAsync(string path, string text, IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        if (DiagnosticsSuspended) return;
        await gate.WaitAsync(token).ConfigureAwait(false);
        var refresh = false;
        try
        {
            if (DiagnosticsSuspended || !IsReady || !Supports(path))
            {
                return;
            }
            await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            refresh = Interlocked.Exchange(ref diagnosticRefreshRequired, 0) != 0;
            if (refresh) await SynchronizeAsync(path, text, token, forceReparse: true).ConfigureAwait(false);
            // 当前文档最后同步后，前面的引用方也要刷新；重开未变文本只更新自己的诊断。
            foreach (var document in documents.Where(d => Supports(d.Path) && (!refresh || !d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            {
                await SynchronizeAsync(document.Path, document.Text, token, forceReparse: refresh).ConfigureAwait(false);
            }
        }
        catch
        {
            if (refresh) Interlocked.Exchange(ref diagnosticRefreshRequired, 1);
            throw;
        }
        finally { gate.Release(); }
    }
}

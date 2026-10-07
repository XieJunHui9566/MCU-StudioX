namespace StudioX.Application.CodeIntelligence;

using System.Collections.Concurrent;
using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private readonly ConcurrentDictionary<string, (int Version, string Text, long Revision)> diagnosticDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CodeDiagnosticBatch> diagnosticBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> invalidatedDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodeDiagnosticBatch> pendingDiagnosticBatches = new(StringComparer.OrdinalIgnoreCase);
    private int languageGeneration;
    private readonly object diagnosticStateLock = new();
    private int diagnosticsSuspended, diagnosticRefreshRequired;
    private long diagnosticRevision, diagnosticWorkRevision;
    private bool deferDiagnosticPublication;

    public bool DiagnosticsSuspended => Volatile.Read(ref diagnosticsSuspended) != 0;

    public IReadOnlyList<CodeDiagnosticBatch> GetDiagnostics()
    {
        lock (diagnosticStateLock)
        {
            return DiagnosticsSuspended || !IsReady ? [] : diagnosticBatches.Values.ToArray();
        }
    }

    /// <summary>编辑或关闭依赖文档时立即撤销整个工作区的诊断；不等待防抖或语言服务排队。</summary>
    public void InvalidateDiagnostics()
    {
        lock (diagnosticStateLock)
        {
            InvalidateDiagnosticsCore();
        }
    }

    private void InvalidateDiagnosticsCore()
    {
        diagnosticRevision++;
        diagnosticBatches.Clear();
        inactiveRegionBatches.Clear();
        pendingDiagnosticBatches.Clear();
        foreach (var uri in diagnosticDocuments.Keys)
        {
            invalidatedDiagnostics[uri] = 0;
        }
        Interlocked.Exchange(ref diagnosticRefreshRequired, 1);
    }

    private int ResetDiagnosticSession()
    {
        lock (diagnosticStateLock)
        {
            var generation = Interlocked.Increment(ref languageGeneration);
            diagnosticDocuments.Clear();
            diagnosticBatches.Clear();
            inactiveRegionBatches.Clear();
            pendingDiagnosticBatches.Clear();
            invalidatedDiagnostics.Clear();
            diagnosticWorkRevision = diagnosticRevision;
            deferDiagnosticPublication = false;
            return generation;
        }
    }

    /// <summary>调试期间暂停实时诊断收集；保留语言导航，恢复时重新解析，避免旧消息重新出现。</summary>
    public void SetDiagnosticsSuspended(bool suspended)
    {
        lock (diagnosticStateLock)
        {
            if (DiagnosticsSuspended == suspended)
            {
                return;
            }
            Volatile.Write(ref diagnosticsSuspended, suspended ? 1 : 0);
            InvalidateDiagnosticsCore();
        }
    }

    private int TrackDiagnosticText(string uri, string text)
    {
        lock (diagnosticStateLock)
        {
            var next = ++version;
            diagnosticDocuments[uri] = (next, text, diagnosticWorkRevision);
            invalidatedDiagnostics.TryRemove(uri, out _);
            diagnosticBatches.TryRemove(uri, out _);
            inactiveRegionBatches.Remove(uri);
            pendingDiagnosticBatches.Remove(uri);
            return next;
        }
    }

    private void InvalidateDependentDiagnostics(string changedUri)
    {
        lock (diagnosticStateLock)
        {
            foreach (var uri in diagnosticDocuments.Keys.Where(uri => !uri.Equals(changedUri, StringComparison.OrdinalIgnoreCase)))
            {
                invalidatedDiagnostics[uri] = 0;
                diagnosticBatches.TryRemove(uri, out _);
                inactiveRegionBatches.Remove(uri);
                pendingDiagnosticBatches.Remove(uri);
            }
        }
    }

    private void CloseDiagnosticDocument(string uri)
    {
        lock (diagnosticStateLock)
        {
            diagnosticDocuments.TryRemove(uri, out _);
            diagnosticBatches.TryRemove(uri, out _);
            inactiveRegionBatches.Remove(uri);
            pendingDiagnosticBatches.Remove(uri);
            invalidatedDiagnostics.TryRemove(uri, out _);
        }
    }

    private bool NeedsDiagnosticReparse(string path)
    {
        var uri = new Uri(ResolveDocumentPath(path)).AbsoluteUri;
        lock (diagnosticStateLock)
        {
            return !diagnosticDocuments.TryGetValue(uri, out var current) || current.Revision != diagnosticRevision || invalidatedDiagnostics.ContainsKey(uri);
        }
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
                !diagnosticDocuments.TryGetValue(uri, out var expected) || expected.Version != publishedVersion ||
                expected.Revision != Volatile.Read(ref diagnosticRevision) || invalidatedDiagnostics.ContainsKey(uri))
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
            var rawDiagnostics = parameters.GetProperty("diagnostics");
            var complete = rawDiagnostics.GetArrayLength() <= 1000;
            if (!complete)
            {
                log.Enqueue("clangd 诊断超过 1000 条显示上限；此次结果不完整。");
            }
            foreach (var item in rawDiagnostics.EnumerateArray().Take(1000))
            {
                try
                {
                    var range = JsonSerializer.Deserialize<CodeRange>(item.GetProperty("range"), JsonStore.Options);
                    if (range is null || range.Start is null || range.End is null)
                    {
                        throw new JsonException("诊断缺少有效范围。");
                    }
                    var start = CodePositions.ToOffset(expected.Text, range.Start);
                    var end = CodePositions.ToOffset(expected.Text, range.End);
                    if (end < start)
                    {
                        throw new ArgumentException("诊断范围结束位置早于开始位置。");
                    }
                    items.Add(new(range, item.TryGetProperty("severity", out var severity) ? severity.GetInt32() : 1,
                        String(item, "message"), String(item, "source", "clangd"), item.TryGetProperty("code", out var code) ? code.ToString() : "", item.Clone()));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                {
                    complete = false;
                    // 单条坏范围不能丢掉同一批的其它有效错误；原始条目和异常仍可追查。
                    log.Enqueue("忽略无效 clangd 诊断条目：" + item.GetRawText() + "\n" + ex);
                }
            }
            lock (diagnosticStateLock)
            {
                if (!DiagnosticsSuspended && generation == Volatile.Read(ref languageGeneration) && diagnosticDocuments.TryGetValue(uri, out var current) &&
                    current.Version == publishedVersion && current.Revision == diagnosticRevision && !invalidatedDiagnostics.ContainsKey(uri))
                {
                    var batch = new CodeDiagnosticBatch(root, relative, expected.Text, publishedVersion, items) { IsComplete = complete };
                    if (deferDiagnosticPublication)
                    {
                        pendingDiagnosticBatches[uri] = batch;
                    }
                    else
                    {
                        diagnosticBatches[uri] = batch;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or StudioXException)
        {
            log.Enqueue("无法读取 clangd 诊断：" + parameters.GetRawText() + "\n" + ex);
        }
    }

    public async Task SynchronizeDiagnosticsAsync(string path, string text, IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        if (DiagnosticsSuspended)
        {
            return;
        }
        var requestedRevision = Volatile.Read(ref diagnosticRevision);
        await gate.WaitAsync(token).ConfigureAwait(false);
        var synchronizing = false;
        try
        {
            if (DiagnosticsSuspended || !Supports(path) || requestedRevision != Volatile.Read(ref diagnosticRevision))
            {
                return;
            }
            await RefreshEnvironmentCoreAsync(token).ConfigureAwait(false);
            if (!IsReady)
            {
                return;
            }
            lock (diagnosticStateLock)
            {
                if (requestedRevision != diagnosticRevision)
                {
                    return;
                }
                diagnosticWorkRevision = requestedRevision;
                deferDiagnosticPublication = true;
                pendingDiagnosticBatches.Clear();
                synchronizing = true;
            }
            var refresh = Interlocked.Exchange(ref diagnosticRefreshRequired, 0) != 0;
            await SynchronizeWorkspaceAsync(path, text, documents, token, refreshEnvironment: false).ConfigureAwait(false);
            if (refresh && NeedsDiagnosticReparse(path))
            {
                await SynchronizeAsync(path, text, token, forceReparse: true).ConfigureAwait(false);
            }
            // 当前文档最后同步后，前面的引用方也要刷新；重开未变文本只更新自己的诊断。
            foreach (var document in documents.Where(d => Supports(d.Path) && (!refresh || !d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            {
                await SynchronizeAsync(document.Path, document.Text, token, forceReparse: refresh && NeedsDiagnosticReparse(document.Path)).ConfigureAwait(false);
            }
            var inactive = new List<CodeInactiveRegionBatch>();
            foreach (var documentPath in documents.Select(d => d.Path).Append(path).Where(Supports).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await ReadInactiveCodeCoreAsync(documentPath, requestedRevision, token).ConfigureAwait(false) is { } batch)
                {
                    inactive.Add(batch);
                }
            }
            lock (diagnosticStateLock)
            {
                if (requestedRevision == diagnosticRevision && !DiagnosticsSuspended)
                {
                    foreach (var (uri, batch) in pendingDiagnosticBatches)
                    {
                        if (diagnosticDocuments.TryGetValue(uri, out var current) && current.Version == batch.Version &&
                        current.Revision == diagnosticRevision && !invalidatedDiagnostics.ContainsKey(uri))
                        {
                            diagnosticBatches[uri] = batch;
                        }
                    }
                    foreach (var batch in inactive)
                    {
                        inactiveRegionBatches[new Uri(ResolveDocumentPath(batch.Path)).AbsoluteUri] = batch;
                    }
                    deferDiagnosticPublication = false;
                }
                pendingDiagnosticBatches.Clear();
            }
        }
        catch
        {
            // 部分同步或取消不能被当作完整依赖快照；下一轮必须重新解析全部引用方。
            lock (diagnosticStateLock)
            {
                if (synchronizing && requestedRevision == diagnosticRevision)
                {
                    InvalidateDiagnosticsCore();
                }
            }
            throw;
        }
        finally { gate.Release(); }
    }
}

namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Application.Editing;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    public async Task<IReadOnlyList<WorkspaceFileChange>> FormatAsync(WorkspaceBufferSnapshot buffer, int? start, int length,
        IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(buffer.Source.RelativePath, buffer.Text, documents, token).ConfigureAwait(false);
            var options = new
            {
                tabSize = 4,
                insertSpaces = true
            };
            object request = start is { } offset
                ? new
                {
                    textDocument = new
                    {
                        uri
                    },
                    options,
                    range = new
                    {
                        start = LspPosition(CodePositions.FromOffset(buffer.Text, offset)),
                        end = LspPosition(CodePositions.FromOffset(buffer.Text, offset + length))
                    }
                }
                : new
                {
                    textDocument = new
                    {
                        uri
                    },
                    options
                };
            var response = await connection!.RequestAsync(start is null ? "textDocument/formatting" : "textDocument/rangeFormatting", request, token).ConfigureAwait(false);
            return response.ValueKind == JsonValueKind.Array ? PlanEdits(buffer, response) : [];
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CodeActionPlan>> QuickFixesAsync(WorkspaceBufferSnapshot buffer, int offset,
        IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(buffer.Source.RelativePath, buffer.Text, documents, token).ConfigureAwait(false);
            var at = CodePositions.FromOffset(buffer.Text, offset);
            var diagnostics = diagnosticBatches.TryGetValue(uri, out var batch) && batch.Text == buffer.Text
                ? batch.Items.Where(d => d.Range.Start.Line <= at.Line && d.Range.End.Line >= at.Line && d.Raw is not null).Select(d => d.Raw!.Value).ToArray()
                : [];
            var response = await connection!.RequestAsync("textDocument/codeAction", new
            {
                textDocument = new
                {
                    uri
                },
                range = new
                {
                    start = LspPosition(at),
                    end = LspPosition(at)
                },
                context = new
                {
                    diagnostics,
                    only = new[] { "quickfix" },
                    triggerKind = 1
                }
            }, token).ConfigureAwait(false);
            var result = new List<CodeActionPlan>();
            if (response.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var action in response.EnumerateArray().Take(50))
            {
                if (!action.TryGetProperty("edit", out var edit) || action.TryGetProperty("disabled", out _))
                {
                    continue;
                }
                var all = new List<WorkspaceFileChange>();
                void Add(string address, JsonElement edits)
                {
                    if (!new Uri(address).AbsoluteUri.Equals(uri, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new StudioXException("FIX_SCOPE", "此修复还涉及其它文件，未应用任何修改。");
                    }
                    all.AddRange(PlanEdits(buffer, edits));
                }
                if (edit.TryGetProperty("changes", out var changes))
                {
                    foreach (var property in changes.EnumerateObject())
                    {
                        Add(property.Name, property.Value);
                    }
                }
                else if (edit.TryGetProperty("documentChanges", out var documentChanges))
                {
                    foreach (var change in documentChanges.EnumerateArray())
                    {
                        if (!change.TryGetProperty("textDocument", out var document))
                        {
                            throw new StudioXException("FIX_OPERATION", "此修复包含文件操作，未应用。");
                        }
                        if (document.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number &&
                            (!diagnosticDocuments.TryGetValue(uri, out var current) || current.Version != v.GetInt32()))
                        {
                            throw new StudioXException("FIX_VERSION", "修复版本已过期，请重试。");
                        }
                        Add(document.GetProperty("uri").GetString()!, change.GetProperty("edits"));
                    }
                }
                if (all.Count > 0)
                {
                    result.Add(new(String(action, "title"), all));
                }
            }
            return result;
        }
        finally { gate.Release(); }
    }

    private static object LspPosition(CodePosition position) => new { line = position.Line, character = position.Character };
    private static IReadOnlyList<WorkspaceFileChange> PlanEdits(WorkspaceBufferSnapshot buffer, JsonElement values)
    {
        var edits = values.EnumerateArray().Select(edit =>
        {
            var range = edit.GetProperty("range").Deserialize<CodeRange>(JsonStore.Options)!;
            var start = CodePositions.ToOffset(buffer.Text, range.Start);
            var end = CodePositions.ToOffset(buffer.Text, range.End);
            return new TextSearchMatch(start, end - start, edit.GetProperty("newText").GetString()!);
        }).ToArray();
        var after = WorkspaceEditService.ApplyText(buffer.Text, edits);
        return after == buffer.Text ? [] : [new(buffer.Source, buffer.Text, after, edits, true)];
    }

    public async Task<IReadOnlyList<CodeLocation>> SearchSymbolsAsync(string query, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsReady)
            {
                return [];
            }
            var response = await connection!.RequestAsync("workspace/symbol", new
            {
                query
            }, token).ConfigureAwait(false);
            var result = new List<CodeLocation>();
            if (response.ValueKind != JsonValueKind.Array)
            {
                return result;
            }
            foreach (var symbol in response.EnumerateArray().Take(300))
            {
                var location = symbol.GetProperty("location");
                if (!location.TryGetProperty("range", out var range))
                {
                    continue;
                }
                var address = new Uri(location.GetProperty("uri").GetString()!);
                if (!address.IsFile || !IsInside(projectRoot, address.LocalPath))
                {
                    continue;
                }
                var relative = Path.GetRelativePath(projectRoot, address.LocalPath).Replace('\\', '/');
                _ = PathBoundary.Resolve(projectRoot, relative);
                result.Add(new(relative, range.Deserialize<CodeRange>(JsonStore.Options)!, String(symbol, "name") + " — " + relative));
            }
            return result;
        }
        finally { gate.Release(); }
    }
}

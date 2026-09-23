namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Foundation;

public sealed record CodeDocumentSymbol(string Name, string Detail, int Kind, CodeRange Range,
    CodeRange SelectionRange, IReadOnlyList<CodeDocumentSymbol> Children);

public sealed partial class CodeIntelligenceService
{
    /// <summary>函数展开时才读取该函数 AST，避免每次编辑都传输整个文件的语法树。</summary>
    public async Task<IReadOnlyList<CodeDocumentSymbol>> FunctionVariablesAsync(string path, string text, CodeRange function,
        CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(path)) return [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!supportsAst) throw new StudioXException("LANGUAGE_AST_UNAVAILABLE", "当前语言服务不支持局部变量列表。");
            var uri = await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            var response = await connection!.RequestAsync("textDocument/ast", new
            {
                textDocument = new { uri },
                range = new { start = new { line = function.Start.Line, character = function.Start.Character }, end = new { line = function.End.Line, character = function.End.Character } }
            }, token).ConfigureAwait(false);
            var result = new List<CodeDocumentSymbol>();
            var start = CodePositions.ToOffset(text, function.Start); var end = CodePositions.ToOffset(text, function.End);
            Read(response, 0);
            return result.DistinctBy(s => (s.Name, s.Range)).OrderBy(s => s.Range.Start.Line).ThenBy(s => s.Range.Start.Character).ToArray();

            void Read(JsonElement node, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (node.ValueKind != JsonValueKind.Object || depth > 128 || result.Count >= 2000) return;
                var kind = String(node, "kind"); var name = String(node, "detail");
                if (String(node, "role") == "declaration" && kind is "Var" or "ParmVar" && name.Length > 0 &&
                    node.TryGetProperty("range", out var value) && ReadOutlineRange(value, text) is { } range)
                {
                    var from = CodePositions.ToOffset(text, range.Start); var to = CodePositions.ToOffset(text, range.End);
                    if (from >= start && to <= end)
                    {
                        var declaration = text[from..Math.Min(to, from + 160)].Split('\n')[0].TrimEnd('\r');
                        // AST 范围覆盖声明本身，跳转到声明起点；不靠正则猜测同名类型/变量的位置。
                        result.Add(new(name, (kind == "ParmVar" ? "参数 · " : "局部变量 · ") + declaration, 13, range, new(range.Start, range.Start), []));
                    }
                }
                if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var child in children.EnumerateArray()) Read(child, depth + 1);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>只返回当前内存文档的声明；不使用工作区搜索，避免把头文件库函数混入列表。</summary>
    public async Task<IReadOnlyList<CodeDocumentSymbol>> DocumentSymbolsAsync(string path, string text,
        CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(path)) return [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            var response = await connection!.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } }, token).ConfigureAwait(false);
            return ReadDocumentSymbols(response, text, uri, token);
        }
        finally { gate.Release(); }
    }

    private static IReadOnlyList<CodeDocumentSymbol> ReadDocumentSymbols(JsonElement response, string text, string uri, CancellationToken token, int depth = 0)
    {
        if (response.ValueKind != JsonValueKind.Array || depth > 64) return [];
        var result = new List<CodeDocumentSymbol>();
        foreach (var item in response.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = String(item, "name");
            if (name.Length == 0 || !item.TryGetProperty("kind", out var kind) || !kind.TryGetInt32(out var number)) continue;
            var rangeOwner = item;
            if (item.TryGetProperty("location", out var location))
            {
                if (String(location, "uri") != uri) continue;
                rangeOwner = location;
            }
            if (!rangeOwner.TryGetProperty("range", out var rangeJson)) continue;
            var range = ReadOutlineRange(rangeJson, text);
            var selection = item.TryGetProperty("selectionRange", out var selectionJson) ? ReadOutlineRange(selectionJson, text) : range;
            if (range is null || selection is null) continue;
            var children = item.TryGetProperty("children", out var childJson) ? ReadDocumentSymbols(childJson, text, uri, token, depth + 1) : [];
            result.Add(new(name, String(item, "detail"), number, range, selection, children));
        }
        return result.OrderBy(item => item.Range.Start.Line).ThenBy(item => item.Range.Start.Character).ToArray();
    }

    private static CodeRange? ReadOutlineRange(JsonElement value, string text)
    {
        try
        {
            var range = JsonSerializer.Deserialize<CodeRange>(value, JsonStore.Options);
            if (range?.Start is null || range.End is null) return null;
            return CodePositions.ToOffset(text, range.Start) <= CodePositions.ToOffset(text, range.End) ? range : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException) { return null; }
    }
}

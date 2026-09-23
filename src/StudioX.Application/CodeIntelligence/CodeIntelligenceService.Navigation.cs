namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private async Task<string> SynchronizeWorkspaceAsync(string path, string text, IReadOnlyList<CodeDocumentSnapshot>? documents, CancellationToken token)
    {
        if (documents is not null)
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { new Uri(ResolveDocumentPath(path)).AbsoluteUri };
            foreach (var document in documents.Where(d => Supports(d.Path)))
            {
                token.ThrowIfCancellationRequested();
                var uri = new Uri(ResolveDocumentPath(document.Path)).AbsoluteUri;
                retained.Add(uri);
                if (!document.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    await SynchronizeAsync(document.Path, document.Text, token).ConfigureAwait(false);
                }
            }
            foreach (var uri in synchronizedDocuments.Keys.Where(uri => !retained.Contains(uri)).ToArray())
            {
                var file = ResolveDocumentPath(new Uri(uri).LocalPath);
                if (File.Exists(file))
                {
                    var disk = await ReadNavigationDocumentAsync(new(file, new(new(0, 0), new(0, 0)), file), token).ConfigureAwait(false);
                    // clangd 关闭文档后仍保留索引；丢弃编辑时先让索引恢复为磁盘内容。
                    if (synchronizedDocuments[uri] != disk.Text) await SynchronizeAsync(file, disk.Text, token, forceReparse: true).ConfigureAwait(false);
                }
                await connection!.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } }, token).ConfigureAwait(false);
                synchronizedDocuments.Remove(uri);
                documentsNeedingReparse.Remove(uri);
                documentsNeedingReparse.UnionWith(synchronizedDocuments.Keys);
            }
        }
        // clangd 不会因为另一个标签中的头文件改变而立即重建当前文件的 AST。
        // 刷新引用方，确保未保存头文件的行号、符号以及关闭时丢弃的修改不会残留。
        return await SynchronizeAsync(path, text, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeLocation>> NavigateAsync(string path, string text, int offset, bool declaration,
        CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(path)) return [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            return await LocationsAsync(uri, CodePositions.FromOffset(text, offset), declaration, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<CodeHover?> HoverAsync(string path, string text, int offset, CancellationToken token = default, IReadOnlyList<CodeDocumentSnapshot>? documents = null)
    {
        if (!Supports(path)) return null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            var position = CodePositions.FromOffset(text, offset);
            var response = await connection!.RequestAsync("textDocument/hover", Parameters(uri, position), token).ConfigureAwait(false);
            var contents = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("contents", out var value) ? HoverText(value) : "";
            var locations = await LocationsAsync(uri, position, declaration: true, token).ConfigureAwait(false);
            if (locations.Count == 0) locations = await LocationsAsync(uri, position, declaration: false, token).ConfigureAwait(false);
            return contents.Length == 0 && locations.Count == 0 ? null : new(contents, locations);
        }
        finally { gate.Release(); }
    }

    private static object Parameters(string uri, CodePosition position) => new { textDocument = new { uri }, position = new { line = position.Line, character = position.Character } };
    private async Task<IReadOnlyList<CodeLocation>> LocationsAsync(string uri, CodePosition position, bool declaration, CancellationToken token)
    {
        var response = await connection!.RequestAsync(declaration ? "textDocument/declaration" : "textDocument/definition", Parameters(uri, position), token).ConfigureAwait(false);
        var items = response.ValueKind == JsonValueKind.Array ? response.EnumerateArray().ToArray() : response.ValueKind == JsonValueKind.Object ? [response] : [];
        var result = new List<CodeLocation>();
        foreach (var item in items)
        {
            var targetUri = String(item, "targetUri", String(item, "uri"));
            if (!Uri.TryCreate(targetUri, UriKind.Absolute, out var locationUri) || !locationUri.IsFile || locationUri.IsUnc) continue;
            var rangeName = item.TryGetProperty("targetSelectionRange", out var range) ? "targetSelectionRange" : "range";
            if (rangeName == "range" && !item.TryGetProperty("range", out range)) continue;
            try
            {
                var path = ResolveDocumentPath(locationUri.LocalPath);
                var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
                var insideProject = IsInside(projectRoot, path);
                var documentPath = insideProject ? relative : path.Replace('\\', '/');
                var display = insideProject ? relative : "内置头文件/" + Path.GetRelativePath(compilerHeaders is not null && IsInside(compilerHeaders, path) ? compilerHeaders : Path.Combine(runtimeDirectory, "languages"), path).Replace('\\', '/');
                var span = JsonSerializer.Deserialize<CodeRange>(range, JsonStore.Options);
                if (span is not null && span.Start.Line >= 0 && span.Start.Character >= 0 && span.End.Line >= span.Start.Line && span.End.Character >= 0)
                    result.Add(new(documentPath, span, display));
            }
            catch (StudioXException ex) { log.Enqueue("跳转位置不可访问：" + ex.Message); }
        }
        return result.Distinct().Take(30).ToArray();
    }

    private static string HoverText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n\n", content.EnumerateArray().Select(HoverText)),
        JsonValueKind.Object => String(content, "value"),
        _ => ""
    };

    private static bool IsInside(string root, string path) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private string ResolveDocumentPath(string path)
    {
        if (!Path.IsPathRooted(path)) return PathBoundary.Resolve(projectRoot, path);
        var full = Path.GetFullPath(path);
        if (IsInside(projectRoot, full)) return PathBoundary.Resolve(projectRoot, Path.GetRelativePath(projectRoot, full).Replace('\\', '/'));
        var languageRoot = Path.GetFullPath(Path.Combine(runtimeDirectory, "languages"));
        if (IsInside(languageRoot, full) && Supports(full)) return PathBoundary.Resolve(languageRoot, Path.GetRelativePath(languageRoot, full).Replace('\\', '/'));
        if (compilerHeaders is not null && IsInside(compilerHeaders, full) && Supports(full)) return PathBoundary.Resolve(compilerHeaders, Path.GetRelativePath(compilerHeaders, full).Replace('\\', '/'));
        throw new StudioXException("LANGUAGE_LOCATION", "跳转目标不在当前工程或内置头文件目录中。");
    }

    public async Task<SourceDocument> ReadNavigationDocumentAsync(CodeLocation location, CancellationToken token = default)
    {
        var path = ResolveDocumentPath(location.DocumentPath);
        var files = new ProjectFileService();
        if (IsInside(projectRoot, path)) return await files.ReadAsync(projectRoot, Path.GetRelativePath(projectRoot, path).Replace('\\', '/'), token).ConfigureAwait(false);
        var document = await files.ReadAsync(runtimeDirectory, Path.GetRelativePath(runtimeDirectory, path).Replace('\\', '/'), token).ConfigureAwait(false);
        return document with { RelativePath = path.Replace('\\', '/'), IsReadOnly = true, ReadOnlyReason = "只读 · 内置工具链头文件" };
    }
}

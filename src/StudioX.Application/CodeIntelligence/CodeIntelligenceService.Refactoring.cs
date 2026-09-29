namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Application.Editing;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    public async Task<IReadOnlyList<CodeLocation>> ReferencesAsync(string path, string text, int offset,
        IReadOnlyList<CodeDocumentSnapshot> documents, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var uri = await SynchronizeWorkspaceAsync(path, text, documents, token).ConfigureAwait(false);
            var at = CodePositions.FromOffset(text, offset);
            var response = await connection!.RequestAsync("textDocument/references", new
            {
                textDocument = new
                {
                    uri
                },
                position = new
                {
                    line = at.Line,
                    character = at.Character
                },
                context = new
                {
                    includeDeclaration = true
                }
            }, token).ConfigureAwait(false);
            var locations = new List<CodeLocation>();
            if (response.ValueKind != JsonValueKind.Array)
            {
                return locations;
            }
            foreach (var item in response.EnumerateArray().Take(10000))
            {
                var target = new Uri(item.GetProperty("uri").GetString()!);
                if (!target.IsFile || target.IsUnc)
                {
                    continue;
                }
                try
                {
                    var full = ResolveDocumentPath(target.LocalPath);
                    var document = IsInside(projectRoot, full) ? Path.GetRelativePath(projectRoot, full).Replace('\\', '/') : full;
                    locations.Add(new(document, JsonSerializer.Deserialize<CodeRange>(item.GetProperty("range"), JsonStore.Options)!, document));
                }
                catch (StudioXException ex) { log.Enqueue("引用位置不可访问：" + ex.Message); }
            }
            return locations.Distinct().ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<WorkspaceFileChange>> RenameAsync(string path, string text, int offset, string oldName, string newName,
        IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
    {
        if (!Regex.IsMatch(newName, @"^[A-Za-z_][A-Za-z_0-9]*$", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("RENAME_IDENTIFIER", "请输入由英文字母、数字和下划线组成的 C/C++ 标识符，不能以数字开头。");
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var files = new ProjectFileService();
            var editing = new WorkspaceEditService(files);
            // 在发出语义请求前记录候选文件，拒绝把旧索引坐标应用到稍后变化的磁盘文本。
            var candidates = await editing.SearchAsync(projectRoot, new(oldName, true, true, false), null,
                "*.c;*.h;*.cpp;*.cc;*.cxx;*.hpp;*.hh;*.hxx", "", true, buffers, token).ConfigureAwait(false);
            var uri = await SynchronizeWorkspaceAsync(path, text, buffers.Select(b => new CodeDocumentSnapshot(b.Source.RelativePath, b.Text)).ToArray(), token).ConfigureAwait(false);
            var at = CodePositions.FromOffset(text, offset);
            var response = await connection!.RequestAsync("textDocument/rename", new
            {
                textDocument = new
                {
                    uri
                },
                position = new
                {
                    line = at.Line,
                    character = at.Character
                },
                newName
            }, token).ConfigureAwait(false);
            if (response.ValueKind is JsonValueKind.Null)
            {
                return [];
            }
            var edits = new Dictionary<string, List<TextSearchMatch>>(StringComparer.OrdinalIgnoreCase);
            void Add(string address, JsonElement values, int? documentVersion = null)
            {
                var target = new Uri(address);
                if (!target.IsFile || target.IsUnc || !IsInside(projectRoot, target.LocalPath))
                {
                    throw new StudioXException("RENAME_SCOPE", "重命名涉及工程外文件，未应用任何修改。");
                }
                var relative = Path.GetRelativePath(projectRoot, target.LocalPath).Replace('\\', '/');
                _ = PathBoundary.Resolve(projectRoot, relative);
                var source = candidates.Files.SingleOrDefault(c => c.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))
                    ?? throw new StudioXException("RENAME_SNAPSHOT", "重命名位置不属于请求前的文本快照，请等待索引完成后重试：" + relative);
                if (documentVersion is not null && (!diagnosticDocuments.TryGetValue(address, out var synced) || synced.Version != documentVersion))
                {
                    throw new StudioXException("RENAME_VERSION", "语言服务返回了过期文档版本，请重新预览。");
                }
                if (!edits.TryGetValue(relative, out var list))
                {
                    edits[relative] = list = [];
                }
                foreach (var edit in values.EnumerateArray())
                {
                    var range = JsonSerializer.Deserialize<CodeRange>(edit.GetProperty("range"), JsonStore.Options)!;
                    var start = CodePositions.ToOffset(source.Before, range.Start);
                    var end = CodePositions.ToOffset(source.Before, range.End);
                    if (end < start || source.Before[start..end] != oldName)
                    {
                        throw new StudioXException("RENAME_SNAPSHOT", "符号位置与文本快照不一致，未修改文件：" + relative);
                    }
                    list.Add(new(start, end - start, edit.GetProperty("newText").GetString()!));
                }
            }
            if (response.TryGetProperty("documentChanges", out var changes))
            {
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("textDocument", out var document))
                    {
                        throw new StudioXException("RENAME_OPERATION", "此重命名包含文件创建/移动操作，当前不支持。");
                    }
                    Add(document.GetProperty("uri").GetString()!, change.GetProperty("edits"),
                        document.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null);
                }
            }
            else if (response.TryGetProperty("changes", out var changesByUri))
            {
                foreach (var property in changesByUri.EnumerateObject())
                {
                    Add(property.Name, property.Value);
                }
            }
            var result = new List<WorkspaceFileChange>();
            foreach (var (relative, values) in edits)
            {
                var candidate = candidates.Files.Single(c => c.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (candidate.Source.IsReadOnly)
                {
                    throw new StudioXException("RENAME_READ_ONLY", "重命名涉及只读文件：" + relative);
                }
                result.Add(candidate with
                {
                    After = WorkspaceEditService.ApplyText(candidate.Before, values),
                    Matches = values
                });
            }
            await editing.ValidateAsync(projectRoot, result, buffers, token).ConfigureAwait(false);
            return result;
        }
        finally { gate.Release(); }
    }
}

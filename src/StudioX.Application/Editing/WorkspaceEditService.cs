namespace StudioX.Application.Editing;

using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>工程文本搜索及修改计划；排除构建产物和链接，应用时重新核对整个计划。</summary>
public sealed class WorkspaceEditService(ProjectFileService files)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".studiox", ".build", "build", "out", "bin", "obj", "node_modules", "__pycache__", ".venv", "venv" };

    public Task<WorkspaceSearchResult> SearchAsync(string project, TextSearchOptions options, string? replacement,
        string include, string exclude, bool includeDevice, IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
        => Task.Run(async () =>
        {
            // 即使工程没有文件，也要明确报告非法正则表达式和通配规则。
            _ = TextSearchService.Find("", options, replacement);
            var included = Globs(include);
            var excluded = Globs(exclude);
            var open = buffers.Where(b => !Path.IsPathRooted(b.Source.RelativePath)).ToDictionary(b => b.Source.RelativePath, StringComparer.OrdinalIgnoreCase);
            var result = new List<WorkspaceFileChange>();
            var notices = new List<string>();
            var directories = new Stack<string>();
            directories.Push("");
            var scanned = 0;
            var visited = 0;
            var matches = 0;
            long retained = 0;
            while (directories.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 10000)
                {
                    throw Limit();
                }
                foreach (var entry in files.List(project, directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsLink)
                    {
                        Notice(entry.RelativePath + "：已跳过链接");
                        continue;
                    }
                    if (entry.IsDirectory)
                    {
                        if (!Excluded.Contains(entry.Name) && !entry.Name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase) &&
                            (includeDevice || !entry.RelativePath.Equals("device", StringComparison.OrdinalIgnoreCase)))
                        {
                            directories.Push(entry.RelativePath);
                        }
                        continue;
                    }
                    if (entry.Name.StartsWith(".studiox-", StringComparison.Ordinal) || entry.Name.Contains(".tmp-", StringComparison.Ordinal) ||
                        !Matches(included, entry.RelativePath, true) || Matches(excluded, entry.RelativePath, false))
                    {
                        continue;
                    }
                    if (++scanned > 10000)
                    {
                        throw Limit();
                    }
                    SourceDocument source;
                    var wasOpen = open.TryGetValue(entry.RelativePath, out var buffer);
                    try
                    {
                        source = buffer?.Source ?? await files.ReadAsync(project, entry.RelativePath, token).ConfigureAwait(false);
                    }
                    catch (StudioXException ex) when (ex.Code is "EDITOR_BINARY" or "EDITOR_ENCODING" or "EDITOR_FILE_SIZE")
                    {
                        Notice(entry.RelativePath + "：" + ex.Message);
                        continue;
                    }
                    var text = buffer?.Text ?? source.Text;
                    var found = TextSearchService.Find(text, options, replacement);
                    if (found.Count == 0)
                    {
                        continue;
                    }
                    var after = replacement is null ? text : ApplyText(text, found);
                    retained += (long)text.Length + after.Length;
                    matches += found.Count;
                    if (matches > 10000 || result.Count >= 500 || retained > 32 * 1024 * 1024)
                    {
                        throw Limit();
                    }
                    result.Add(new(source, text, after, found, wasOpen));
                }
            }
            return new WorkspaceSearchResult(result.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(), notices, scanned);
            void Notice(string message)
            {
                if (notices.Count < 50)
                {
                    notices.Add(message);
                }
            }
        }, token);

    public static string ApplyText(string text, IReadOnlyList<TextSearchMatch> edits)
    {
        var ordered = edits.OrderBy(e => e.Start).ToArray();
        var builder = new StringBuilder();
        var end = 0;
        foreach (var edit in ordered)
        {
            if (edit.Start < end || edit.Length < 0 || edit.Start > text.Length - edit.Length)
            {
                throw new StudioXException("WORKSPACE_EDIT_RANGE", "修改位置无效或互相重叠，未修改文件。");
            }
            builder.Append(text, end, edit.Start - end).Append(edit.Replacement);
            end = edit.Start + edit.Length;
        }
        builder.Append(text, end, text.Length - end);
        if (builder.Length > 4 * 1024 * 1024)
        {
            throw Limit();
        }
        return builder.ToString();
    }

    public async Task ValidateAsync(string project, IReadOnlyList<WorkspaceFileChange> changes,
        IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
    {
        if (changes.Count > 500 || changes.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
        {
            throw Limit();
        }
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            var disk = await files.ReadAsync(project, change.Path, token).ConfigureAwait(false);
            var open = buffers.FirstOrDefault(b => b.Source.RelativePath.Equals(change.Path, StringComparison.OrdinalIgnoreCase));
            if (!change.CanApply || disk.IsReadOnly || disk.DiskHash != change.Source.DiskHash ||
                (open?.Text ?? disk.Text) != change.Before || (change.WasOpen && open is null))
            {
                throw new StudioXException("WORKSPACE_EDIT_STALE", change.Path + " 在预览后已变化、被关闭或为只读；请重新生成预览，所有文件均未修改。");
            }
        }
    }

    private static Regex[] Globs(string patterns) => patterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(p => new Regex("^" + Regex.Escape(p.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))).ToArray();
    private static bool Matches(Regex[] patterns, string path, bool empty) => patterns.Length == 0 ? empty : patterns.Any(p => p.IsMatch(path) || p.IsMatch(Path.GetFileName(path)));
    private static StudioXException Limit() => new("WORKSPACE_SEARCH_LIMIT", "工程搜索达到范围上限（10,000 文件/匹配、500 个结果文件、32 Mi 字符），请用文件过滤缩小范围；未修改文件。");
}

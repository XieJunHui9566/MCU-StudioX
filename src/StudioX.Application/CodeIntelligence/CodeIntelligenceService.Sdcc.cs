namespace StudioX.Application.CodeIntelligence;

public sealed partial class CodeIntelligenceService
{
    private bool sdccAnalysis;
    private readonly HashSet<string> sdccDiskHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, long Modified, string Text)> sdccHeaderCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<CodeDocumentSnapshot>> SdccDocumentsAsync(string path, string text,
        IReadOnlyList<CodeDocumentSnapshot>? documents, CancellationToken token)
    {
        var snapshots = new Dictionary<string, CodeDocumentSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (documents is null)
        {
            foreach (var pair in synchronizedDocuments)
            {
                var file = new Uri(pair.Key).LocalPath;
                if (!sdccDiskHeaders.Contains(file))
                {
                    snapshots[file] = new(file, pair.Value);
                }
            }
        }
        else
        {
            foreach (var document in documents)
            {
                snapshots[ResolveDocumentPath(document.Path)] = document;
            }
        }
        snapshots[ResolveDocumentPath(path)] = new(path, text);
        sdccDiskHeaders.Clear();
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    if (entry.Name is not (".git" or ".build" or ".studiox" or "build" or "Build" or "Debug" or "Release"))
                    {
                        pending.Push(entry.FullName);
                    }
                    continue;
                }
                if (!entry.Extension.Equals(".h", StringComparison.OrdinalIgnoreCase) || snapshots.ContainsKey(entry.FullName))
                {
                    continue;
                }
                var file = (FileInfo)entry;
                if (!sdccHeaderCache.TryGetValue(file.FullName, out var cached) || cached.Length != file.Length || cached.Modified != file.LastWriteTimeUtc.Ticks)
                {
                    cached = (file.Length, file.LastWriteTimeUtc.Ticks, await File.ReadAllTextAsync(file.FullName, token));
                    sdccHeaderCache[file.FullName] = cached;
                }
                if (SdccAssemblyProjection.Create(cached.Text) == cached.Text)
                {
                    continue;
                }
                // 头文件也使用内存文档，否则只投影 main.c 无法修复头文件里的 NOP 宏。
                snapshots[file.FullName] = new(file.FullName, cached.Text);
                sdccDiskHeaders.Add(file.FullName);
            }
        }
        return snapshots.Values.ToArray();
    }
}

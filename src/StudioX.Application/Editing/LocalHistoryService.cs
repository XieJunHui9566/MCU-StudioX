namespace StudioX.Application.Editing;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>保存和批量修改前记录文本；有限保留，恢复只生成编辑计划。</summary>
public sealed class LocalHistoryService(string dataDirectory)
{
    private readonly string root = Path.Combine(dataDirectory, "local-history");
    private readonly SemaphoreSlim gate = new(1, 1);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string DirectoryFor(string project, string path)
    {
        _ = PathBoundary.Resolve(project, path);
        return Path.Combine(root, Hash(Path.GetFullPath(project).ToUpperInvariant()), Hash(path.Replace('\\', '/').ToUpperInvariant()));
    }
    public async Task CaptureAsync(string project, string path, string text, string reason, CancellationToken token = default)
    {
        if (text.Length > 4 * 1024 * 1024)
        {
            throw new StudioXException("HISTORY_SIZE", "文本超过本地历史大小限制，未修改原文件。");
        }
        var directory = DirectoryFor(project, path);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var existing = Directory.EnumerateFiles(directory, "*.json").OrderDescending(StringComparer.Ordinal).ToArray();
            var hash = Hash(text);
            if (existing.Length > 0 && (await JsonStore.ReadAsync<LocalHistoryEntry>(existing[0], token).ConfigureAwait(false)).Hash == hash)
            {
                return;
            }
            var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N");
            await JsonStore.WriteAsync(Path.Combine(directory, id + ".json"), new LocalHistoryEntry(id, path, DateTimeOffset.UtcNow, reason, hash, text), token).ConfigureAwait(false);
            long size = 0;
            var count = 0;
            foreach (var item in Directory.EnumerateFiles(directory, "*.json").OrderDescending(StringComparer.Ordinal))
            {
                size += new FileInfo(item).Length;
                if (++count > 50 || size > 32L * 1024 * 1024)
                {
                    File.Delete(item);
                }
            }
        }
        finally { gate.Release(); }
    }
    public async Task<IReadOnlyList<LocalHistoryEntry>> ListAsync(string project, string path, CancellationToken token = default)
    {
        var directory = DirectoryFor(project, path);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = new List<LocalHistoryEntry>();
            if (!Directory.Exists(directory))
            {
                return result;
            }
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderDescending(StringComparer.Ordinal).Take(50))
            {
                if (new FileInfo(file).Length > 32L * 1024 * 1024)
                {
                    throw new StudioXException("HISTORY_SIZE", "历史文件过大。");
                }
                var entry = await JsonStore.ReadAsync<LocalHistoryEntry>(file, token).ConfigureAwait(false);
                if (!entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || Hash(entry.Text) != entry.Hash)
                {
                    throw new StudioXException("HISTORY_HASH", "本地历史内容校验失败，未应用。");
                }
                result.Add(entry);
            }
            return result;
        }
        finally { gate.Release(); }
    }
    public static WorkspaceFileChange RestorePlan(WorkspaceBufferSnapshot buffer, LocalHistoryEntry entry)
    {
        if (!buffer.Source.RelativePath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) || Hash(entry.Text) != entry.Hash)
        {
            throw new StudioXException("HISTORY_TARGET", "历史版本不属于当前文件或已损坏。");
        }
        return new(buffer.Source, buffer.Text, entry.Text, [new(0, buffer.Text.Length, entry.Text)], true);
    }
}

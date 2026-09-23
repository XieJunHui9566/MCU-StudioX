namespace StudioX.Application;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;
using StudioX.Engine;

public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, bool IsLink);
public sealed record SourceDocument(string RelativePath, string Text, Encoding Encoding, string DiskHash, bool IsReadOnly, string? ReadOnlyReason = null);

public sealed partial class ProjectFileService
{
    public IReadOnlyList<ProjectEntry> List(string project, string relativeDirectory = "")
    {
        var directory = relativeDirectory.Length == 0 ? Path.GetFullPath(project) : PathBoundary.Resolve(project, relativeDirectory);
        return new DirectoryInfo(directory).EnumerateFileSystemInfos()
            .Where(item => item.Name != ".git" && !item.Name.StartsWith(".studiox-copy-", StringComparison.Ordinal) && !item.Name.StartsWith(".studiox-rename-", StringComparison.Ordinal))
            .Select(item => new ProjectEntry(item.Name, Path.GetRelativePath(project, item.FullName).Replace('\\', '/'),
                (item.Attributes & FileAttributes.Directory) != 0, (item.Attributes & FileAttributes.ReparsePoint) != 0))
            .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<SourceDocument> ReadAsync(string project, string relativePath, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(project, relativePath);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new StudioXException("EDITOR_FILE_SIZE", "当前编辑器支持不超过 4 MiB 的文本文件。");
        var bytes = await File.ReadAllBytesAsync(path, token);
        // 按 BOM 保留编码；无 BOM 仅接受严格 UTF-8，防止保存时悄悄替换原始字节。
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(true, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        string text;
        try { text = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new StudioXException("EDITOR_ENCODING", "文件不是 UTF-8 / 带 BOM 的 UTF-16 文本，暂不支持此编码。"); }
        if (text.Contains('\0')) throw new StudioXException("EDITOR_BINARY", "此文件是二进制文件，无法作为源代码打开。");
        var managed = CMakeGenerator.IsManagedFile(relativePath, text);
        return new(relativePath, text, encoding, Convert.ToHexString(SHA256.HashData(bytes)), managed || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
            managed ? "只读 · 器件支持配置；用户配置请修改根目录 CMakeLists.txt" : null);
    }

    public async Task<SourceDocument> SaveAsync(string project, SourceDocument document, string text, CancellationToken token = default)
    {
        if (document.IsReadOnly) throw new StudioXException("EDITOR_READ_ONLY", "此文件为只读文件。");
        var path = PathBoundary.Resolve(project, document.RelativePath);
        var bytes = document.Encoding.GetPreamble().Concat(document.Encoding.GetBytes(text)).ToArray();
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            var disk = await File.ReadAllBytesAsync(path, token);
            if (Convert.ToHexString(SHA256.HashData(disk)) != document.DiskHash)
                throw new StudioXException("EDITOR_FILE_CHANGED", "磁盘文件已被其他程序修改，未覆盖。请保留当前修改后重新打开文件。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return document with { Text = text, DiskHash = Convert.ToHexString(SHA256.HashData(bytes)) };
    }
}

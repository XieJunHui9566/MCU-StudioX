namespace StudioX.Engine;

using System.Buffers;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;
using StudioX.Foundation;

/// <summary>按签名读取 7z 或既有 ZIP；固实块按顺序解压，避免逐文件反复解压整个 SDK。</summary>
public sealed class ToolchainArchive : IDisposable
{
    public const int MaximumFiles = 200000;
    public const long MaximumBytes = 40L * 1024 * 1024 * 1024;
    public const int MaximumManifestBytes = 32 * 1024 * 1024;
    private readonly ZipArchive? zip;
    private readonly IArchive? sevenZip;
    private readonly Dictionary<string, ToolchainArchiveEntry> index = new(StringComparer.Ordinal);
    public IReadOnlyList<ToolchainArchiveEntry> Entries
    {
        get;
    }
    public long Bytes
    {
        get;
    }
    public string Container => sevenZip is null ? "ZIP" : "7z";

    private ToolchainArchive(Stream stream, CancellationToken token)
    {
        Span<byte> signature = stackalloc byte[6];
        stream.Position = 0;
        if (stream.Read(signature) != signature.Length)
        {
            throw new StudioXException("TOOLS_ARCHIVE_FORMAT", "开发环境组件归档不完整。");
        }
        stream.Position = 0;
        try
        {
            var entries = new List<ToolchainArchiveEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var validationRoot = Path.Combine(Path.GetTempPath(), "studiox-archive-" + Guid.NewGuid().ToString("N"));
            long total = 0;
            void Add(string name, long size, bool directory, int attributes, int extendedAttributes = 0,
                bool encrypted = false, bool anti = false, bool incomplete = false, string? link = null)
            {
                token.ThrowIfCancellationRequested();
                if (encrypted)
                {
                    throw new StudioXException("TOOLS_ARCHIVE_ENCRYPTED", "开发环境组件不能使用加密或带密码的归档。");
                }
                if ((attributes & (int)FileAttributes.ReparsePoint) != 0 || ((attributes >> 16) & 0xf000) == 0xa000 ||
                    ((extendedAttributes >> 16) & 0xf000) == 0xa000 || link is not null)
                {
                    throw new StudioXException("TOOLS_ARCHIVE_LINK", "工具归档不接受链接或重解析点。");
                }
                if (directory || name.EndsWith('/') || anti || incomplete || !names.Add(name))
                {
                    throw new StudioXException("TOOLS_ARCHIVE_ENTRY", "归档含重复、目录、删除标记或不完整条目。");
                }
                _ = PathBoundary.Resolve(validationRoot, name);
                if (size < 0 || size > MaximumBytes - total || entries.Count >= MaximumFiles)
                {
                    throw new StudioXException("TOOLS_ARCHIVE_SIZE", "归档文件数或展开大小超出限制。");
                }
                total += size;
                var entry = new ToolchainArchiveEntry(name, size);
                index.Add(name, entry);
                entries.Add(entry);
            }
            if (signature.SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }))
            {
                SevenZipHeaderGuard.Validate(stream, token);
                sevenZip = SevenZipArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false });
                foreach (var file in sevenZip.Entries)
                {
                    var seven = (SevenZipArchiveEntry)file;
                    Add(file.Key ?? "", file.Size, file.IsDirectory, file.Attrib ?? 0, seven.ExtendedAttrib ?? 0,
                        // 0.50.3 将没有压缩流的空文件误报为加密；空内容没有需要解密的数据。
                        file.Size != 0 && file.IsEncrypted, seven.IsAnti, !file.IsComplete || file.IsSplitAfter, file.LinkTarget);
                }
            }
            else if (signature[0] == 0x50 && signature[1] == 0x4b && signature[2] is 3 or 5 && signature[3] is 4 or 6)
            {
                zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var file in zip.Entries)
                {
                    Add(file.FullName, file.Length, file.FullName.EndsWith('/'), file.ExternalAttributes);
                }
            }
            else
            {
                throw new StudioXException("TOOLS_ARCHIVE_FORMAT", "开发环境组件仅支持 7z 和已有 ZIP 容器；不接受自解压程序或分卷。");
            }
            // Windows 上文件不能同时充当父目录；在写入任何文件之前检查此类冲突。
            foreach (var name in names)
            {
                for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                {
                    if (names.Contains(name[..slash]))
                    {
                        throw new StudioXException("TOOLS_ARCHIVE_ENTRY", "归档内文件与父目录路径冲突。");
                    }
                }
            }
            if (entries.Count < 2)
            {
                throw new StudioXException("TOOLS_ARCHIVE_SIZE", "开发环境组件归档必须包含清单和工具文件。");
            }
            Entries = entries;
            Bytes = total;
        }
        catch (Exception error)
        {
            zip?.Dispose();
            sevenZip?.Dispose();
            if (error is StudioXException or OperationCanceledException or OutOfMemoryException)
            {
                throw;
            }
            throw new StudioXException("TOOLS_ARCHIVE", "无法读取开发环境组件归档；归档已损坏或压缩配置不受支持。", error);
        }
    }

    public static ToolchainArchive Open(Stream stream, CancellationToken token = default) => new(stream, token);

    public async Task<byte[]> ReadManifestAsync(CancellationToken token = default)
    {
        if (!index.TryGetValue("toolset.json", out var entry))
        {
            throw new StudioXException("TOOLS_ARCHIVE", "缺少 toolset.json。");
        }
        if (entry.Length > MaximumManifestBytes)
        {
            throw new StudioXException("TOOLS_ARCHIVE_SIZE", "工具清单过大。");
        }
        using var memory = new MemoryStream();
        using var content = zip is not null ? zip.GetEntry(entry.Name)!.Open() : sevenZip!.Entries.Single(e => e.Key == entry.Name).OpenEntryStream();
        await CopyExactAsync(content, memory, entry.Length, token).ConfigureAwait(false);
        return memory.ToArray();
    }

    public void ValidateIndex(IReadOnlyDictionary<string, string> hashes)
    {
        if (hashes.Count != Entries.Count - 1 || hashes.ContainsKey("toolset.json") ||
            Entries.Any(entry => entry.Name != "toolset.json" && !hashes.ContainsKey(entry.Name)))
        {
            throw new StudioXException("TOOLS_ARCHIVE_ENTRY", "索引与归档文件集合不一致。");
        }
    }

    public async Task ReadFilesAsync(Func<ToolchainArchiveEntry, Stream, Task> consume, CancellationToken token = default)
    {
        if (zip is not null)
        {
            foreach (var file in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                using var content = file.Open();
                await consume(index[file.FullName], content).ConfigureAwait(false);
            }
        }
        else
        {
            using var reader = sevenZip!.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested();
                using var content = reader.OpenEntryStream();
                await consume(index[reader.Entry.Key!], content).ConfigureAwait(false);
            }
        }
    }

    public static async Task CopyExactAsync(Stream source, Stream output, long length, CancellationToken token = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            long copied = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // LZMA 解码使用同步 Read；工作在后台任务，避免上游异步解码器状态问题。
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (read > length - copied)
                {
                    throw new StudioXException("TOOLS_ARCHIVE_SIZE", "文件实际展开大小超过归档声明。");
                }
                copied += read;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (copied != length)
            {
                throw new StudioXException("TOOLS_ARCHIVE", "归档文件内容不完整。");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Dispose()
    {
        zip?.Dispose();
        sevenZip?.Dispose();
    }
}

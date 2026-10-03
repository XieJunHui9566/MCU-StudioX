namespace StudioX.Engine;

using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using StudioX.Foundation;

/// <summary>自包含的 7z/LZMA2 导出器；轻量版也不依赖系统安装的 7-Zip。</summary>
public static class ToolchainArchiveWriter
{
    public static Task WriteAsync(string root, IEnumerable<string> files, string output,
        IProgress<string>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072);
        // 每文件独立压缩，导出时内存有界，读取清单不必解压整个 SDK。
        using var writer = new SevenZipWriter(destination, new SevenZipWriterOptions(CompressionType.LZMA2));
        foreach (var relative in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("导出 7z：" + relative);
            using var file = File.OpenRead(PathBoundary.Resolve(root, relative));
            using var input = new CancellableInput(file, token);
            writer.Write(relative, input, null);
        }
        token.ThrowIfCancellationRequested();
    }, token);

    private sealed class CancellableInput(Stream input, CancellationToken token) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return input.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return input.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

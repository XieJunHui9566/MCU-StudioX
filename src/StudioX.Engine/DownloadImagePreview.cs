namespace StudioX.Engine;

/// <summary>一次明确授权下载中的单个映像；地址是实际写入地址，字节数是映像文件大小。</summary>
public sealed record DownloadImagePreview(string RelativePath, string Format, string Sha256,
    long Bytes, ulong Address, string Role);

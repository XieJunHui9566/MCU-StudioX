namespace StudioX.Application.Distribution;

/// <summary>缓存大小仅表示已落盘的字节；使用前仍须重新校验完整 SHA-256。</summary>
public sealed record DistributionDownloadState(long SavedBytes, long TotalBytes, bool Complete)
{
    public string ToText() => Complete ? "已有完整缓存，使用前重新校验"
        : SavedBytes > 0 ? $"已保存 {SavedBytes:N0}/{TotalBytes:N0} 字节，可继续下载" : "尚未下载";
}

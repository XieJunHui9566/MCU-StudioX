namespace StudioX.Engine;

/// <summary>经校验后复制到本次下载目录的映像，后续工程编辑不能替换它。</summary>
public sealed record DownloadImageSnapshot(DownloadImagePreview Preview, string Path, ulong VerificationBytes);

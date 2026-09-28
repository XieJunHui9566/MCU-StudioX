namespace StudioX.Engine;

public sealed record DownloadPreview(DownloadConfiguration Configuration, DownloadOptions Options,
    string SourceImage, string Format, string Sha256, long ImageBytes)
{
    public IReadOnlyList<DownloadImagePreview> Images { get; init; } = [];
    public string ApprovalSha256 => Images.Count > 0 ? DownloadImageLayout.ApprovalSha256(Images) : Sha256;
}

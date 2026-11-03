namespace StudioX.Engine.Pic;

/// <summary>HEX 导出凭据只表示软件构建一致性，不授予烧录授权。</summary>
internal sealed record PicBuildReceipt(int FormatVersion, string SourceSha256, string ToolFingerprint,
    string HexRelativePath, string HexSha256, string ElfRelativePath, string ElfSha256,
    string? BuildToolsFingerprint = null, string Debugger = "none", string? SourceRoot = null, string? AnalysisInputsSha256 = null)
{
    public const string RelativePath = ".studiox/pic-build.json";
}

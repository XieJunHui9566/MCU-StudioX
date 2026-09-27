namespace StudioX.Application.Mcp;

/// <summary>本次有界 PDF 搜索的进度与命中项，用于从上一页继续读取。</summary>
public sealed record PdfSearchResult(int ScannedThroughPage, int PagesWithoutExtractableText,
    IReadOnlyList<PdfSearchHit> Matches);

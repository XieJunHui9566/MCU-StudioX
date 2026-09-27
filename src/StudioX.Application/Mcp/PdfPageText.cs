namespace StudioX.Application.Mcp;

/// <summary>一页 PDF 的可提取文字；页码从 1 开始，无文字不代表页面没有图形内容。</summary>
public sealed record PdfPageText(int PageNumber, int PageCount, string Text, bool HasExtractableText);

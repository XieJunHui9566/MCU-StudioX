namespace StudioX.Application.Mcp;

/// <summary>文字匹配所在页与页内字符偏移；页码从 1 开始，字符偏移从 0 开始。</summary>
public sealed record PdfSearchHit(int Page, int OffsetCharacters, string Snippet);

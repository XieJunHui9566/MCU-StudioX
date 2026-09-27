namespace StudioX.Application.Mcp;

/// <summary>PDF 页数、文件字节数与内容哈希；页数经过读取服务的边界校验。</summary>
public sealed record PdfDocumentInfo(int PageCount, long Bytes, string Sha256);

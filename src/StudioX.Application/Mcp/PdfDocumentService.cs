namespace StudioX.Application.Mcp;

using System.Drawing;
using System.Security.Cryptography;
using PDFtoImage;
using SkiaSharp;
using StudioX.Foundation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

public sealed record PdfDocumentInfo(int PageCount, long Bytes, string Sha256);
public sealed record PdfPageText(int PageNumber, int PageCount, string Text, bool HasExtractableText);
public sealed record PdfSearchHit(int Page, int OffsetCharacters, string Snippet);
public sealed record PdfSearchResult(int ScannedThroughPage, int PagesWithoutExtractableText,
    IReadOnlyList<PdfSearchHit> Matches);
public sealed record PdfRenderedRegion(int PageNumber, int PixelWidth, int PixelHeight, byte[] PngBytes);

/// <summary>有界读取 PDF 的文字或页面区域；工具层负责批准与校验路径所属工程。</summary>
public static class PdfDocumentService
{
    private const int MaxPdfBytes = 32 * 1024 * 1024;
    private const int MaxPages = 2_000;
    private const int MaxPngBytes = 700 * 1024;
    private const int MaxPageTextChars = 512_000;

    public static PdfDocumentInfo Inspect(string path, CancellationToken token = default)
    {
        var bytes = ReadCheckedPdf(path, token);
        using var document = OpenPdf(bytes, token);
        return new PdfDocumentInfo(document.NumberOfPages, bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static PdfPageText ReadPageText(string path, int page, CancellationToken token = default)
    {
        var bytes = ReadCheckedPdf(path, token);
        using var document = OpenPdf(bytes, token);
        RequirePage(page, document.NumberOfPages);
        var text = ExtractText(document, page, token);
        return new PdfPageText(page, document.NumberOfPages, text, !string.IsNullOrWhiteSpace(text));
    }

    public static PdfSearchResult Search(string path, string query, int startPage, int maxPages,
        int maxHits, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 120 ||
            query.Any(char.IsControl) || maxPages is < 1 or > 12 || maxHits is < 1 or > 12)
            throw new StudioXException("MCP_PDF_SEARCH", "PDF 查询词或扫描范围无效。");
        var bytes = ReadCheckedPdf(path, token);
        using var document = OpenPdf(bytes, token);
        RequirePage(startPage, document.NumberOfPages);
        var hits = new List<PdfSearchHit>();
        var missingText = 0;
        var lastPage = startPage - 1;
        for (var page = startPage; page <= Math.Min(document.NumberOfPages, startPage + maxPages - 1); page++)
        {
            token.ThrowIfCancellationRequested();
            var text = ExtractText(document, page, token);
            lastPage = page;
            if (string.IsNullOrWhiteSpace(text)) { missingText++; continue; }
            var offset = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (offset >= 0)
            {
                var begin = Math.Max(0, offset - 90);
                var end = Math.Min(text.Length, offset + query.Length + 120);
                var snippet = text[begin..end].Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (hits.Count < maxHits)
                    hits.Add(new PdfSearchHit(page, offset, snippet));
            }
        }
        return new PdfSearchResult(lastPage, missingText, hits);
    }

    public static PdfRenderedRegion RenderRegion(string path, int page, double x, double y,
        double width, double height, int maxDimension, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new StudioXException("MCP_PDF_PLATFORM", "PDF 页面渲染当前仅支持 Windows。");
        if (maxDimension is < 256 or > 1_600 ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1.000001 || y + height > 1.000001)
            throw new StudioXException("MCP_PDF_REGION", "PDF 裁切区域无效；坐标须为页面左上角起算的 0–1 比例。");

        var bytes = ReadCheckedPdf(path, token);
        using (var document = OpenPdf(bytes, token))
            RequirePage(page, document.NumberOfPages);
        token.ThrowIfCancellationRequested();
        try
        {
            var pageSize = Conversion.GetPageSize(bytes, new Index(page - 1));
            if (!float.IsFinite(pageSize.Width) || !float.IsFinite(pageSize.Height) ||
                pageSize.Width <= 0 || pageSize.Height <= 0)
                throw new StudioXException("MCP_PDF_RENDER", "PDF 页面的尺寸无效。");
            var bounds = new RectangleF((float)(x * pageSize.Width), (float)(y * pageSize.Height),
                (float)(width * pageSize.Width), (float)(height * pageSize.Height));
            var longSide = Math.Max(bounds.Width, bounds.Height);
            var dimension = maxDimension;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pixelWidth = Math.Max(1, (int)Math.Round(dimension * bounds.Width / longSide));
                var pixelHeight = Math.Max(1, (int)Math.Round(dimension * bounds.Height / longSide));
                var options = new RenderOptions(Width: pixelWidth, Height: pixelHeight,
                    Bounds: bounds, DpiRelativeToBounds: true, AntiAliasing: PdfAntiAliasing.All,
                    BackgroundColor: SKColors.White);
                using var bitmap = Conversion.ToImage(bytes, new Index(page - 1), options: options);
                if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1 ||
                    bitmap.Width > 1_600 || bitmap.Height > 1_600)
                    throw new StudioXException("MCP_PDF_RENDER", "PDF 页面渲染失败或图像尺寸超限。");
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                if (encoded is null)
                    throw new StudioXException("MCP_PDF_RENDER", "PDF 页面无法编码为 PNG 图像。");
                if (encoded.Size <= MaxPngBytes)
                {
                    token.ThrowIfCancellationRequested();
                    return new PdfRenderedRegion(page, bitmap.Width, bitmap.Height, encoded.ToArray());
                }
                if (dimension <= 320)
                    throw new StudioXException("MCP_PDF_IMAGE_SIZE",
                        "PDF 区域渲染后的 PNG 超过 700 KiB；请缩小裁切区域。");
                dimension = Math.Max(320, (int)(dimension * 0.75));
            }
        }
        catch (StudioXException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            throw new StudioXException("MCP_PDF_RENDER", "PDF 页面无法渲染；文件可能损坏或加密。", ex);
        }
    }

    private static byte[] ReadCheckedPdf(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("MCP_PDF_PATH", "PDF 路径必须是已授权的绝对 .pdf 文件路径。");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length is < 5 or > MaxPdfBytes)
                throw new StudioXException("MCP_PDF_SIZE", "PDF 文件必须小于等于 32 MiB。");
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                    throw new StudioXException("MCP_PDF_FILE", "PDF 文件读取时发生变化，请重新打开。");
                offset += read;
            }
            if (!bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                throw new StudioXException("MCP_PDF_FORMAT", "文件不是有效的 PDF 格式。");
            token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (StudioXException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex)
        {
            throw new StudioXException("MCP_PDF_FILE", "PDF 文件无法读取或已被占用。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StudioXException("MCP_PDF_FILE", "没有权限读取 PDF 文件。", ex);
        }
    }

    private static PdfDocument OpenPdf(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var document = PdfDocument.Open(bytes);
            if (document.NumberOfPages is < 1 or > MaxPages)
            {
                document.Dispose();
                throw new StudioXException("MCP_PDF_PAGES", "PDF 页数必须在 1–2000 页之间。");
            }
            token.ThrowIfCancellationRequested();
            return document;
        }
        catch (StudioXException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            throw new StudioXException("MCP_PDF_FORMAT", "PDF 文件无法解析；文件可能损坏或加密。", ex);
        }
    }

    private static string ExtractText(PdfDocument document, int page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var text = ContentOrderTextExtractor.GetText(document.GetPage(page));
            token.ThrowIfCancellationRequested();
            if (text.Length > MaxPageTextChars)
                throw new StudioXException("MCP_PDF_TEXT_SIZE", "该 PDF 页面的文字超过 512000 字符，无法安全读取。");
            return text;
        }
        catch (StudioXException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (OutOfMemoryException) { throw; }
        catch (Exception ex)
        {
            throw new StudioXException("MCP_PDF_TEXT", "PDF 页面文字无法提取；可能需要查看渲染图像。", ex);
        }
    }

    private static void RequirePage(int page, int count)
    {
        if (page < 1 || page > count)
            throw new StudioXException("MCP_PDF_PAGE", $"PDF 页码必须在 1–{count} 之间。");
    }
}

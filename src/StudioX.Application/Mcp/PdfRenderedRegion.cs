namespace StudioX.Application.Mcp;

/// <summary>PDF 页面的已裁切 PNG 区域；宽高以输出图像像素计。</summary>
public sealed record PdfRenderedRegion(int PageNumber, int PixelWidth, int PixelHeight, byte[] PngBytes);

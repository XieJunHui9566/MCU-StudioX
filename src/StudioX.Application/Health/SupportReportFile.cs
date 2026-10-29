namespace StudioX.Application.Health;

/// <summary>有界文本证据；明确标记缺失、截断与原始读取错误。</summary>
public sealed record SupportReportFile(string Path, long? Bytes, DateTime? ModifiedUtc, string? Text, bool Truncated, string? Error);

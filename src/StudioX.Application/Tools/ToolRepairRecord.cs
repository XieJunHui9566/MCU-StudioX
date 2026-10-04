namespace StudioX.Application.Tools;

internal sealed record ToolRepairRecord(int FormatVersion, string TransactionId, string Id, string Version,
    string CompilerId, string Fingerprint, string? PreviousFingerprint, bool HadPrevious, string State,
    DateTimeOffset CreatedUtc, string Diagnostic = "");

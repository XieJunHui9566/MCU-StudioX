namespace StudioX.Engine;

public sealed record Ag32PinMappingStatus(bool Enabled, string? SourcePath, string? ImagePath,
    bool ReceiptCurrent, string[] Diagnostics, bool LicenseConfigured = false);

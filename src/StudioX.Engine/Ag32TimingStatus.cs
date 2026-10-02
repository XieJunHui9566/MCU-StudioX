namespace StudioX.Engine;

/// <summary>时序展示不是下载授权；下载仍核对完整布线凭据。</summary>
public sealed record Ag32TimingStatus(Ag32TimingState State, string Message,
    Ag32PinClockSettings? Clocks = null, decimal? SetupSlackNs = null, decimal? HoldSlackNs = null,
    int Covered = 0, int Total = 0, string? SetupPath = null, string? HoldPath = null,
    string? ReportPath = null);

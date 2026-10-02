namespace StudioX.Engine.Debugging;

public sealed record FaultEvidence(int FormatVersion, string Device, string Source, DateTimeOffset CapturedAtUtc,
    uint? Cfsr, uint? Hfsr, uint? Mmfar, uint? Bfar, uint? StackedPc, uint? StackedLr,
    string Raw, bool FirmwareMatched = false, IReadOnlyList<uint>? Backtrace = null);

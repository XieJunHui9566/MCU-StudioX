namespace StudioX.Ai8051Isp;

public sealed record IspSessionResult(TargetInfo Target, bool ProgramAcknowledged, bool ConfigurationReadback,
    bool FlashReadback = false, bool RuntimeVerified = false);

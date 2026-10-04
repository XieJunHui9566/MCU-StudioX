namespace StudioX.MakerPlugins;

public sealed record PidMetrics(double OvershootPercent, double? RiseSeconds, double? SettlingSeconds, double TailMeanError,
    double Iae, double Ise, double SaturatedPercent, double? RecoverySeconds, double StepWindowSeconds, double SimulatedSeconds);

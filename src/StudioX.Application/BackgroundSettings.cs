namespace StudioX.Application;

public sealed record BackgroundSettings(int FormatVersion = 1, BackgroundKind Kind = BackgroundKind.None,
    string? Asset = null, double Opacity = 0.65, double Dim = 0.15, double Blur = 0,
    BackgroundFit Fit = BackgroundFit.Fill, bool PauseWhenInactive = true);

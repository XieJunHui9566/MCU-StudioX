namespace StudioX.MakerPlugins;

public sealed record PidTerms(double P, double I, double D, double Output, bool Saturated);

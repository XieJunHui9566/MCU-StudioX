namespace StudioX.MakerPlugins;

public sealed record PidSample(double Time, double Y, double Measured, double P, double I, double D, double Output);

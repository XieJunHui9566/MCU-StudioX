namespace StudioX.Application.SerialPlot;

using StudioX.Devices;
public sealed record PlotPreferences(SerialSettings Connection, PlotFormat Format);

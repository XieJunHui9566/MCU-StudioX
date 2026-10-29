namespace StudioX.Engine;

using StudioX.Ai8051Isp;
public sealed record Mx251IspReport(bool Success, string Summary, string LogPath, IspSessionResult Result);

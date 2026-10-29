namespace StudioX.Ai8051Isp;

public sealed record TargetInfo(string Model, string Bsl, string Magic, uint IrcHz, string RawStatus);

namespace StudioX.Desktop;

public sealed record BreakpointRow(string Id, string File, int Line, bool Enabled, string Status, string Kind, string Rule, int HitCount, string Details, string RequestedLocation = "");

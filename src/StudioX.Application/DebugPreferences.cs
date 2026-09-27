namespace StudioX.Application;

using StudioX.Engine.Debugging;

public sealed record DebugPreferences(int FormatVersion, SourceBreakpoint[] Breakpoints, string[] Watches);

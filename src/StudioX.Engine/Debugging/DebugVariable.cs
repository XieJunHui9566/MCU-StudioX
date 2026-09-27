namespace StudioX.Engine.Debugging;

public sealed record DebugVariable(string Name, string Value, string Type = "", bool Changed = false);

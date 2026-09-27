namespace StudioX.Engine.Debugging;

public sealed record DebugRegister(string Name, string Value, bool Changed = false);

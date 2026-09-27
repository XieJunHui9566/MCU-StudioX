namespace StudioX.Engine.Debugging;

public sealed record DebugFrame(int Level, string Function, string File, int Line, string Address);

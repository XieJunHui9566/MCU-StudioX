namespace StudioX.Engine.Debugging;

public sealed record DebugSnapshot(DebugRegister[] Registers, DebugFrame[] Frames, DebugVariable[] Locals, DebugVariable[] Watches, int SelectedFrame = 0)
{
    public static DebugSnapshot Empty { get; } = new([], [], [], []);
}

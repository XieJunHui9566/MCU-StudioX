namespace StudioX.Engine.Debugging;

public sealed record CoreDumpTask(string Name, uint Tcb, uint? StackStart, uint StackBytes, uint? Flags);

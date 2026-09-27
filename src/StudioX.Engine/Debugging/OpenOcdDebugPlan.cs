namespace StudioX.Engine.Debugging;

public sealed record OpenOcdDebugPlan(string OpenOcd, string[] OpenOcdArguments, string Gdb, string[] GdbArguments, string[] InitializeCommands);

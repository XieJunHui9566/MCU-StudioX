namespace StudioX.Engine;

public sealed record StcIspToolStatus(bool Available, string? ProgrammerExecutable, string? PythonExecutable, string Message);

namespace StudioX.Engine.Arduino;

public sealed record ArduinoCompilationCommand(string Directory, string File, string[] Arguments);

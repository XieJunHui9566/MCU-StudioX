namespace StudioX.Application.Terminal;

public sealed record ProjectTerminalSnapshot(bool Running, string? Directory, string Status, ConsoleSnapshot Screen);

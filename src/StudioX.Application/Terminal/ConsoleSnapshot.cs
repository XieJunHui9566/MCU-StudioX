namespace StudioX.Application.Terminal;

using StudioX.Application.Serial;

public sealed record ConsoleSnapshot(TerminalSnapshot Display, int CursorOffset, bool CursorVisible);

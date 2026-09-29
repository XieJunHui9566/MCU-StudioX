namespace StudioX.Desktop;

internal sealed record WorkbenchCommand(string Title, string Shortcut, Func<bool> Enabled, Func<Task> Execute);

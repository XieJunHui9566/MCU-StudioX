namespace StudioX.KeilImporter;

public sealed record PathPickerRequest(string Title, bool Folder, string InitialPath, string FilterLabel, string FilterPattern);

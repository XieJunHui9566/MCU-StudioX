namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record CompatibilityEdit(string RelativePath, string Before, string After, string Reason);

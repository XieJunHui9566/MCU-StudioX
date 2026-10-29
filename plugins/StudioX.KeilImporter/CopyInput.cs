namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record CopyInput(string Source, string RelativePath, long Bytes, string Sha256);

namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record KeilMemory(string Name, uint Origin, uint Bytes);

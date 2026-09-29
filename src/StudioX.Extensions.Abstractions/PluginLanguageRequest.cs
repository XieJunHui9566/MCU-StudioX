namespace StudioX.Extensions.Abstractions;

public sealed record PluginLanguageRequest(string Operation, string Path, string Text, int Offset, long Revision);

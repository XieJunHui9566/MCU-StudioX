namespace StudioX.Foundation;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    TimeSpan Timeout, IReadOnlyDictionary<string, string>? Environment = null, string? StandardInput = null,
    IReadOnlyList<string>? RemoveEnvironment = null, IProgress<string>? Output = null);

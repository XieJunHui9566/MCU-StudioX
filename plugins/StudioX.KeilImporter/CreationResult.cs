namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record CreationResult(string Directory, string DeviceId, int CopiedFiles, string PreviewId,
    bool CompilationVerified, string BuildLog, string? BuildError);

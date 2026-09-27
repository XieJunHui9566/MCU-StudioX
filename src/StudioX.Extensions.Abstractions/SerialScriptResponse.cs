namespace StudioX.Extensions.Abstractions;

public sealed record SerialScriptResponse(int ApiVersion, long Id, SerialScriptResult? Result = null, string? Error = null);

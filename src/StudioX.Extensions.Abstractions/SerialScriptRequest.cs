namespace StudioX.Extensions.Abstractions;

public sealed record SerialScriptRequest(int ApiVersion, long Id, string Operation, string? Source = null,
    int[]? Bytes = null, string Direction = "RX", string? Timestamp = null, bool Flush = false);

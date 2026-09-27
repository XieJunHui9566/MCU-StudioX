namespace StudioX.Extensions.Abstractions;

public sealed record DecoderResponse(int ProtocolVersion, string RequestId, DecodeResult? Result, string? ErrorCode, string? Error);

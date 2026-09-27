namespace StudioX.Extensions.Abstractions;

public sealed record DecoderRequest(int ProtocolVersion, string RequestId, string PayloadBase64);

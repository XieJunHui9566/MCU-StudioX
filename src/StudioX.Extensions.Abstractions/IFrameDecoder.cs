namespace StudioX.Extensions.Abstractions;

public interface IFrameDecoder
{
    DecodeResult Decode(ReadOnlyMemory<byte> payload);
}
public sealed record SignalValue(string Name, double Value, string Unit);
public sealed record DecodeResult(string Summary, IReadOnlyList<SignalValue> Signals);
public sealed record DecoderRequest(int ProtocolVersion, string RequestId, string PayloadBase64);
public sealed record DecoderResponse(int ProtocolVersion, string RequestId, DecodeResult? Result, string? ErrorCode, string? Error);

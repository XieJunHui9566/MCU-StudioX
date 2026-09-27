namespace StudioX.Extensions.Abstractions;

public interface IFrameDecoder
{
    DecodeResult Decode(ReadOnlyMemory<byte> payload);
}

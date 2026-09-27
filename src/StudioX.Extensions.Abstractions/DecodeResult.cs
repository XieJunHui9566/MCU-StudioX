namespace StudioX.Extensions.Abstractions;

public sealed record DecodeResult(string Summary, IReadOnlyList<SignalValue> Signals);

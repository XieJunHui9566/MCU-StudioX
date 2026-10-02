namespace StudioX.Application.Peripherals;

public sealed record PeripheralReading(string Register, ulong Value, DateTimeOffset RequestedAtUtc, long Revision);

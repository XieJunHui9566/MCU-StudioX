namespace StudioX.Application.Peripherals;

public sealed record PeripheralBinding(int FormatVersion, string DeviceId, string File, string Sha256);

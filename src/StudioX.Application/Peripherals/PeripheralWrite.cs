namespace StudioX.Application.Peripherals;

using StudioX.Engine.Svd;

public sealed record PeripheralWrite(PeripheralDocument Document, SvdRegister Register, ulong Value, long Revision);

namespace StudioX.Application.Peripherals;

using StudioX.Engine.Svd;

public sealed record PeripheralDocument(string Project, PeripheralBinding Binding, SvdDevice Device);

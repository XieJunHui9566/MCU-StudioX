namespace StudioX.Engine.Svd;

public sealed record SvdDevice(string Name, string Description, string Sha256, IReadOnlyList<SvdRegister> Registers);

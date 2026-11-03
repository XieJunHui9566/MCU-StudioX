namespace StudioX.Engine;

public sealed record BuildAssemblyCatalog(string DeviceId, string ReceiptSha256, IReadOnlyList<BuildAssemblyFile> Files);

namespace StudioX.Packages;

public sealed record BundledPackImportResult(int Imported, int Skipped, IReadOnlyList<BundledPackImportFailure> Failures);

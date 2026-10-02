namespace StudioX.Engine.Svd;

public sealed record SvdRegister(string Path, string Peripheral, uint Address, int Width, string Access,
    string Description, ulong? ResetValue, string? ReadAction, string? ModifiedWriteValues, bool WriteConstrained,
    IReadOnlyList<SvdField> Fields)
{
    public bool CanRead => Access is "read-only" or "read-write" or "read-writeOnce";
    public bool CanWrite => (Access is "write-only" or "read-write") && !WriteConstrained &&
        Fields.All(f => f.Access is "write-only" or "read-write");
    public bool HasReadSideEffects => ReadAction is not null || Fields.Any(f => f.ReadAction is not null);
    public string WriteSemantics => string.Join("; ", new[] { ModifiedWriteValues }
        .Concat(Fields.Where(f => f.ModifiedWriteValues is not null).Select(f => f.Name + ": " + f.ModifiedWriteValues))
        .Where(s => s is not null));
}

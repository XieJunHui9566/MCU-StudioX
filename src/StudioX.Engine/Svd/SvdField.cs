namespace StudioX.Engine.Svd;

public sealed record SvdField(string Name, int Offset, int Width, string Access, string Description,
    string? ReadAction, string? ModifiedWriteValues, IReadOnlyDictionary<ulong, string> Enumerations)
{
    public ulong Extract(ulong value) => (value >> Offset) & (Width == 64 ? ulong.MaxValue : (1UL << Width) - 1);
}

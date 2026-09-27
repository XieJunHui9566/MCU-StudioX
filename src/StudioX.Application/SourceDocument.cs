namespace StudioX.Application;

using System.Text;
public sealed record SourceDocument(string RelativePath, string Text, Encoding Encoding, string DiskHash, bool IsReadOnly, string? ReadOnlyReason = null);

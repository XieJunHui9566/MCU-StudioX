namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

internal sealed record BuiltImage(string RelativePath, string Format, string Sha256, string? SymbolsPath = null, string? SymbolsSha256 = null);
internal sealed record BuildReceipt(ProjectManifest Project, string ToolFingerprint, BuiltImage[] Images, string? SourceStamp = null)
{
    internal const string RelativePath = ".build/studiox-build-receipt.json";
    internal static async Task WriteAsync(string root, ProjectManifest project, string fingerprint, string[] images, CancellationToken token, string? sourceStamp = null)
    {
        var entries = new List<BuiltImage>();
        foreach (var image in images)
        {
            var relative = Path.GetRelativePath(root, image).Replace('\\', '/');
            await using var stream = File.OpenRead(PathBoundary.Resolve(root, relative));
            var sdcc = project.ToolsetId == "stc.sdcc";
            var symbols = project.Kind == ProjectKind.CubeMx ? image : sdcc ? "" : Path.ChangeExtension(image, ".elf");
            string? symbolsHash = null;
            if (File.Exists(symbols))
            {
                await using var elf = File.OpenRead(symbols);
                symbolsHash = Convert.ToHexString(await SHA256.HashDataAsync(elf, token));
            }
            entries.Add(new(relative, project.Kind == ProjectKind.CubeMx ? "elf" : sdcc ? "ihex" : "bin", Convert.ToHexString(await SHA256.HashDataAsync(stream, token)),
                symbolsHash is null ? null : Path.GetRelativePath(root, symbols).Replace('\\', '/'), symbolsHash));
        }
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, RelativePath), new BuildReceipt(project, fingerprint, entries.ToArray(), sourceStamp), token);
    }
}

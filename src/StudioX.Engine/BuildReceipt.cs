namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

internal sealed record BuiltImage(string RelativePath, string Format, string Sha256, string? SymbolsPath = null, string? SymbolsSha256 = null);
internal sealed record BuildReceipt(ProjectManifest Project, string ToolFingerprint, BuiltImage[] Images, string? SourceStamp = null,
    EspressifFlashLayout? EspressifLayout = null)
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

    internal static async Task WriteEspressifAsync(string root, ProjectManifest project, string fingerprint,
        EspressifFlashLayout layout, string appBin, string appSymbols, CancellationToken token, string? sourceStamp)
    {
        var appRelative = Path.GetRelativePath(root, appBin).Replace('\\', '/');
        var symbolsRelative = Path.GetRelativePath(root, appSymbols).Replace('\\', '/');
        await using var symbols = File.OpenRead(PathBoundary.Resolve(root, symbolsRelative));
        var symbolsHash = Convert.ToHexString(await SHA256.HashDataAsync(symbols, token));
        // 引导程序、分区表和应用程序共同组成一次下载；每个映像均固定实际生成的偏移和哈希。
        var images = layout.Images.Select(image => new BuiltImage(image.RelativePath, "bin", image.Sha256,
            image.RelativePath.Equals(appRelative, StringComparison.OrdinalIgnoreCase) ? symbolsRelative : null,
            image.RelativePath.Equals(appRelative, StringComparison.OrdinalIgnoreCase) ? symbolsHash : null)).ToArray();
        if (images.Count(image => image.SymbolsPath is not null) != 1)
        {
            throw new StudioXException("ESPRESSIF_APP_IMAGE", "原生下载布局没有包含唯一的应用程序映像。");
        }
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, RelativePath),
            new BuildReceipt(project, fingerprint, images, sourceStamp, layout), token);
    }
}

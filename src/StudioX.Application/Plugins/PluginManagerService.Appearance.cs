namespace StudioX.Application.Plugins;

using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public sealed partial class PluginManagerService
{
    /// <summary>只读取当前已信任应用插件的完整索引插画，路径始终限制在该插件内。</summary>
    public async Task<byte[]> LoadAppearanceArtworkAsync(string id, PluginAppearanceArtwork artwork, CancellationToken token = default)
    {
        ThemeArtworkService.ValidatePlugin(artwork);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var entry = (await ListCoreAsync(token).ConfigureAwait(false)).SingleOrDefault(item => item.Id == id);
            if (entry is not { Enabled: true, CanEnable: true, Manifest.Scope: "application" } ||
                !entry.Manifest.Sha256.TryGetValue(artwork.Asset, out var hash) ||
                !hash.Equals(artwork.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("PLUGIN_ARTWORK_UNTRUSTED", "插画必须属于当前已启用插件的完整哈希索引。");
            }
            var path = PathBoundary.Resolve(Path.GetDirectoryName(entry.ManifestPath)!, artwork.Asset);
            return await ThemeArtworkService.ReadVerifiedAsync(path, artwork.Sha256, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

namespace StudioX.Application;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>校验并保存主题 PNG，不依赖 WPF、不修改壁纸配置或播放会话。</summary>
public sealed partial class ThemeArtworkService(string dataDirectory)
{
    private readonly string dataRoot = Path.GetFullPath(dataDirectory);
    private string Root => PathBoundary.Resolve(dataRoot, "theme-artwork");
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static void Validate(ThemeArtwork artwork)
    {
        if (artwork.Asset is null || !CachedAsset().IsMatch(artwork.Asset) || !ValidOpacity(artwork.Opacity))
        {
            throw new StudioXException("THEME_ARTWORK", "主题插画需要内容哈希命名的 PNG 和 0 到 1 的有限强度。");
        }
    }

    public static void ValidatePlugin(PluginAppearanceArtwork artwork)
    {
        if (artwork.Asset is null || !PluginAsset().IsMatch(artwork.Asset) || artwork.Sha256 is null ||
            !Hash().IsMatch(artwork.Sha256) || !ValidOpacity(artwork.Opacity))
        {
            throw new StudioXException("PLUGIN_ARTWORK", "插画必须为 assets 目录下的 PNG，具有 SHA-256 和有效强度。");
        }
    }

    public static ThemeArtwork FromPlugin(PluginAppearanceArtwork artwork)
    {
        ValidatePlugin(artwork);
        return new(artwork.Sha256.ToLowerInvariant() + ".png", artwork.Opacity);
    }

    public byte[] Read(ThemeArtwork artwork)
    {
        Validate(artwork);
        var path = PathBoundary.Resolve(dataRoot, "theme-artwork/" + artwork.Asset);
        using var source = File.OpenRead(path);
        var bytes = ReadBounded(source);
        Verify(bytes, Path.GetFileNameWithoutExtension(artwork.Asset));
        return bytes;
    }

    public async Task ImportAsync(ThemeArtwork artwork, byte[] bytes, CancellationToken token = default)
    {
        Validate(artwork);
        Verify(bytes, Path.GetFileNameWithoutExtension(artwork.Asset));
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Root);
        var target = PathBoundary.Resolve(dataRoot, "theme-artwork/" + artwork.Asset);
        if (File.Exists(target))
        {
            _ = Read(artwork);
            return;
        }
        var temporary = PathBoundary.Resolve(dataRoot, "theme-artwork/.import-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, target, false);
            }
            catch (IOException) when (File.Exists(target)) { _ = Read(artwork); }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public static async Task<byte[]> ReadVerifiedAsync(string path, string hash, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        CheckLength(source.Length);
        var bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        Verify(bytes, hash);
        return bytes;
    }

    private static byte[] ReadBounded(Stream source)
    {
        CheckLength(source.Length);
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }

    private static void CheckLength(long length)
    {
        if (length is < 33 or > MaximumBytes)
        {
            throw new StudioXException("THEME_ARTWORK_SIZE", "主题插画须为非空 PNG，大小不超过 16 MiB。");
        }
    }

    private static void Verify(byte[] bytes, string hash)
    {
        CheckLength(bytes.Length);
        if (!Hash().IsMatch(hash) || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("THEME_ARTWORK_HASH", "主题插画内容与声明的 SHA-256 不一致。");
        }
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            width is 0 or > 4096 || height is 0 or > 4096 || (long)width * height > 16 * 1024 * 1024)
        {
            throw new StudioXException("THEME_ARTWORK_FORMAT", "插画必须是尺寸不超过 4096、像素不超过 16 Mi 的 PNG。");
        }
    }

    private static bool ValidOpacity(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    [GeneratedRegex("^[a-f0-9]{64}\\.png$", RegexOptions.CultureInvariant)] private static partial Regex CachedAsset();
    [GeneratedRegex("^assets/[a-z0-9][a-z0-9._-]{0,79}\\.png$", RegexOptions.CultureInvariant)] private static partial Regex PluginAsset();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Hash();
}

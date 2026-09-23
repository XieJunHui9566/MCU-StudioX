namespace StudioX.Application;

using System.Security.Cryptography;
using StudioX.Foundation;

public enum BackgroundKind { None, Image, Video }
public enum BackgroundFit { Fill, Fit }
public sealed record BackgroundSettings(int FormatVersion = 1, BackgroundKind Kind = BackgroundKind.None,
    string? Asset = null, double Opacity = 0.65, double Dim = 0.15, double Blur = 0,
    BackgroundFit Fit = BackgroundFit.Fill, bool PauseWhenInactive = true);

/// <summary>媒体拷入用户资源目录；偏好只记录相对文件名，不依赖临时文件或安装目录。</summary>
public sealed class AppearanceService(string dataDirectory)
{
    private readonly string assetRoot = Path.Combine(dataDirectory, "backgrounds");
    private readonly string settingsPath = Path.Combine(dataDirectory, "appearance.json");
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".wmv", ".mov" };

    public static void Validate(BackgroundSettings settings)
    {
        if (settings.FormatVersion != 1 || !Enum.IsDefined(settings.Kind) || !Enum.IsDefined(settings.Fit) ||
            !double.IsFinite(settings.Opacity) || settings.Opacity is < 0 or > 1 ||
            !double.IsFinite(settings.Dim) || settings.Dim is < 0 or > 0.9 ||
            !double.IsFinite(settings.Blur) || settings.Blur is < 0 or > 20)
            throw new StudioXException("APPEARANCE_FORMAT", "背景设置数值无效。");
        if (settings.Kind != BackgroundKind.None && (string.IsNullOrWhiteSpace(settings.Asset) ||
            !(settings.Kind == BackgroundKind.Image ? Images : Videos).Contains(Path.GetExtension(settings.Asset))))
            throw new StudioXException("BACKGROUND_FORMAT", "背景媒体格式不支持。");
    }
    public string? Resolve(BackgroundSettings settings)
    {
        Validate(settings);
        if (settings.Kind == BackgroundKind.None) return null;
        var path = PathBoundary.Resolve(assetRoot, settings.Asset!);
        if (!File.Exists(path)) throw new StudioXException("BACKGROUND_MISSING", "背景文件丢失，请重新选择文件。");
        return path;
    }
    public async Task<BackgroundSettings> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(settingsPath)) return new BackgroundSettings();
        var settings = await JsonStore.ReadAsync<BackgroundSettings>(settingsPath, token);
        _ = Resolve(settings);
        return settings;
    }
    public Task SaveAsync(BackgroundSettings settings, CancellationToken token = default)
    {
        _ = Resolve(settings);
        return JsonStore.WriteAsync(settingsPath, settings, token);
    }
    public async Task<string> ImportAsync(string sourcePath, BackgroundKind kind, CancellationToken token = default)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (kind == BackgroundKind.None || !(kind == BackgroundKind.Image ? Images : Videos).Contains(extension))
            throw new StudioXException("BACKGROUND_FORMAT", "请选择支持的本地图片或视频文件。");
        var limit = kind == BackgroundKind.Image ? 50L * 1024 * 1024 : 512L * 1024 * 1024;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (source.Length == 0 || source.Length > limit) throw new StudioXException("BACKGROUND_SIZE", kind == BackgroundKind.Image ? "图片需小于 50 MiB。" : "视频需小于 512 MiB。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token)).ToLowerInvariant();
        var asset = hash + extension;
        Directory.CreateDirectory(assetRoot);
        var target = PathBoundary.Resolve(assetRoot, asset);
        if (File.Exists(target)) return asset;
        var temporary = Path.Combine(assetRoot, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            source.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                await source.CopyToAsync(output, token);
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return asset;
    }
}

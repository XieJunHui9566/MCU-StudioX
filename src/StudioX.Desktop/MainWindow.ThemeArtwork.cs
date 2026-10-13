namespace StudioX.Desktop;

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    private string? loadedThemeArtwork;

    internal static BitmapImage DecodeThemeArtwork(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 2560;
        image.StreamSource = source;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ApplyThemeArtwork(ThemeDefinition theme)
    {
        var artwork = theme.Artwork;
        if (artwork is null)
        {
            ThemeArtworkImage.Source = null;
            loadedThemeArtwork = null;
            ThemeArtworkImage.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            if (loadedThemeArtwork != artwork.Asset)
            {
                ThemeArtworkImage.Source = DecodeThemeArtwork(services.Themes.Artwork.Read(artwork));
                loadedThemeArtwork = artwork.Asset;
            }
            ThemeArtworkImage.Visibility = artwork.Opacity > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshThemeArtworkOpacity();
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidOperationException or StudioXException)
        {
            ThemeArtworkImage.Source = null;
            loadedThemeArtwork = null;
            ThemeArtworkImage.Visibility = Visibility.Collapsed;
            Log("氛围背景加载失败，保留配色：" + error);
        }
    }

    private void RefreshThemeArtworkOpacity()
    {
        // 视频仍由原背景层持有，插画仅淡化叠加，不重设媒体 Source 或播放位置。
        ThemeArtworkImage.Opacity = (currentTheme.Artwork?.Opacity ?? 0) *
            (backgroundSettings.Kind == BackgroundKind.None ? 1 : .3);
    }
}

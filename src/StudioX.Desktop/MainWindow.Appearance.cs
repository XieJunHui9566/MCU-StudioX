namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using StudioX.Application;

public partial class MainWindow
{
    private BackgroundSettings backgroundSettings = new();
    private string? loadedMediaPath;
    private bool videoReady;
    private Action<string>? mediaFailure;
    private async void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (closing || !pendingOperation.IsCompleted) { Status.Text = "请等待当前操作完成。"; return; }
        await pendingZoomSave;
        if (closing) return;
        var dialog = new AppearanceWindow(services, currentTheme, backgroundSettings, editorSettings, (theme, background, editor) => { ApplyTheme(theme); ApplyBackground(background); ApplyEditorSettings(editor); }) { Owner = this };
        mediaFailure = dialog.ReportMediaError;
        dialog.Activated += (_, _) => UpdateVideoPlayback();
        dialog.Deactivated += (_, _) => UpdateVideoPlayback();
        try { dialog.ShowDialog(); }
        finally { mediaFailure = null; }
        UpdateVideoPlayback();
    }
    public void ApplyBackground(BackgroundSettings settings)
    {
        var path = services.Appearance.Resolve(settings);
        var changed = loadedMediaPath != path || backgroundSettings.Kind != settings.Kind;
        BitmapImage? decodedImage = null;
        if (changed && settings.Kind == BackgroundKind.Image && path is not null)
        {
            decodedImage = new BitmapImage(); decodedImage.BeginInit(); decodedImage.CacheOption = BitmapCacheOption.OnLoad;
            decodedImage.DecodePixelWidth = 2560; decodedImage.UriSource = new Uri(path); decodedImage.EndInit(); decodedImage.Freeze();
        }
        if (changed)
        {
            BackgroundVideo.Close(); BackgroundVideo.Source = null; BackgroundImage.Source = null; videoReady = false;
            BackgroundImage.Visibility = Visibility.Collapsed; BackgroundVideo.Visibility = Visibility.Collapsed;
            if (settings.Kind == BackgroundKind.Image && path is not null)
            {
                // OnLoad 释放图片文件句柄，避免设置对话框关闭后仍锁住本地文件。
                BackgroundImage.Source = decodedImage; BackgroundImage.Visibility = Visibility.Visible;
            }
            else if (settings.Kind == BackgroundKind.Video && path is not null)
            {
                BackgroundVideo.Visibility = Visibility.Visible; BackgroundVideo.Source = new Uri(path);
                // 先打开媒体以触发 MediaOpened，再根据窗口激活状态控制播放。
                BackgroundVideo.Play();
            }
            loadedMediaPath = path;
        }
        backgroundSettings = settings;
        BackgroundLayer.Visibility = settings.Kind == BackgroundKind.None ? Visibility.Collapsed : Visibility.Visible;
        MediaLayer.Opacity = settings.Opacity; BackgroundDim.Opacity = settings.Dim;
        MediaLayer.Effect = settings.Blur > 0 ? new BlurEffect { Radius = settings.Blur, RenderingBias = RenderingBias.Performance } : null;
        BackgroundImage.Stretch = BackgroundVideo.Stretch = settings.Fit == BackgroundFit.Fill ? Stretch.UniformToFill : Stretch.Uniform;
        RefreshSurfaceBrushes(); UpdateVideoPlayback();
    }
    private void RefreshSurfaceBrushes()
    {
        var media = backgroundSettings.Kind != BackgroundKind.None;
        Set("ChromeSurface", "Surface", media ? (byte)240 : (byte)255);
        Set("ToolSurface", "Surface", media ? (byte)195 : (byte)255);
        Set("EditorSurface", "Background", media ? (byte)100 : (byte)255);
        Set("HoverSurface", "Panel", media ? (byte)205 : (byte)255);
        void Set(string key, string source, byte alpha)
        {
            var color = (Color)ColorConverter.ConvertFromString(currentTheme.Colors[source]); color.A = alpha;
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }
    private void UpdateVideoPlayback()
    {
        if (BackgroundVideo is null || backgroundSettings.Kind != BackgroundKind.Video || !videoReady) return;
        var applicationActive = IsActive || OwnedWindows.OfType<Window>().Any(window => window.IsActive);
        if (WindowState == WindowState.Minimized || (backgroundSettings.PauseWhenInactive && !applicationActive)) BackgroundVideo.Pause();
        else BackgroundVideo.Play();
    }
    private void Window_StateChanged(object? sender, EventArgs e) => UpdateVideoPlayback();
    private void Window_Activated(object? sender, EventArgs e) => UpdateVideoPlayback();
    private void Window_Deactivated(object? sender, EventArgs e) => UpdateVideoPlayback();
    private void BackgroundVideo_MediaOpened(object sender, RoutedEventArgs e) { videoReady = true; UpdateVideoPlayback(); }
    private void BackgroundVideo_MediaEnded(object sender, RoutedEventArgs e) { BackgroundVideo.Position = TimeSpan.Zero; UpdateVideoPlayback(); }
    private void BackgroundVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (backgroundSettings.Kind != BackgroundKind.Video) return;
        videoReady = false; BackgroundVideo.Close();
        Status.Text = "背景视频无法播放，请在外观设置中更换视频（推荐 H.264 MP4）。";
        Log("背景视频：" + e.ErrorException);
        mediaFailure?.Invoke(Status.Text);
    }
}

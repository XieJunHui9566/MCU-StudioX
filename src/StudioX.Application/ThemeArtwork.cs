namespace StudioX.Application;

/// <summary>用户主题资源目录中的内容寻址插画，与动态壁纸偏好分开保存。</summary>
public sealed record ThemeArtwork(string Asset, double Opacity = .75);

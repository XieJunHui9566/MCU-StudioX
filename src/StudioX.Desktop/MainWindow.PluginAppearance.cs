namespace StudioX.Desktop;

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public partial class MainWindow
{
    private TypographySettings typographySettings = new();

    private void ApplyTypography(TypographySettings settings)
    {
        TypographyService.Validate(settings);
        settings = TextFontCatalog.Recover(settings);
        typographySettings = settings;
        System.Windows.Application.Current.Resources["WorkbenchFontFamily"] = new FontFamily(settings.FontFamily);
        System.Windows.Application.Current.Resources["WorkbenchFontSize"] = settings.FontSize;
    }

    private FrameworkElement CreatePluginAppearanceView(string pluginId, PluginWorkspaceSession? owner, JsonElement value)
    {
        var catalog = PluginAppearanceCatalog.Parse(value);
        return new PluginAppearanceView(catalog, () => new(currentTheme, typographySettings, editorSettings),
            draft => ApplyPluginAppearanceAsync(pluginId, owner, draft, catalog), PluginManager.Log,
            theme => LoadPluginThemeArtworkAsync(pluginId, catalog, theme, CancellationToken.None));
    }

    private async Task<byte[]?> LoadPluginThemeArtworkAsync(string pluginId, PluginAppearanceDefinition? catalog,
        ThemeDefinition theme, CancellationToken token)
    {
        if (theme.Artwork is null)
        {
            return null;
        }
        ThemeService.Validate(theme);
        try
        {
            return await Task.Run(() => services.Themes.Artwork.Read(theme.Artwork), token);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            var declared = catalog?.Themes.SingleOrDefault(item => item.Id == theme.Id)?.Artwork;
            if (declared is null || ThemeArtworkService.FromPlugin(declared).Asset != theme.Artwork.Asset)
            {
                throw new StudioXException("PLUGIN_ARTWORK_UNDECLARED", "主题插画不属于当前目录且没有已保存的副本。", error);
            }
            return await services.PluginManager.LoadAppearanceArtworkAsync(pluginId, declared, token);
        }
    }

    private async Task ApplyPluginAppearanceAsync(string pluginId, PluginWorkspaceSession? owner, WorkbenchStyle draft,
        PluginAppearanceDefinition? catalog = null)
    {
        bool Current() => !closing && !closed && owner is not null && ReferenceEquals(owner, pluginApplication) &&
            ReferenceEquals(FindPluginSession(pluginId), owner) && CanRunPlugin(pluginId);
        if (!Current())
        {
            throw new StudioXException("PLUGIN_INACTIVE", "外观插件已停用，请从当前已启用的插件页面操作。");
        }
        if (!pendingOperation.IsCompleted)
        {
            throw new StudioXException("APPEARANCE_BUSY", "请等待当前工作台操作完成。");
        }
        // 保存前再次拦截，不能依赖下拉列表过滤而让迟到或构造的数据写入符号字体。
        TextFontCatalog.Validate(draft.Typography.FontFamily);
        TextFontCatalog.Validate(draft.Editor.FontFamily);
        await pendingZoomSave;
        if (!Current())
        {
            throw new StudioXException("PLUGIN_INACTIVE", "外观插件会话已经结束。");
        }
        var style = draft with
        {
            Editor = editorSettings with
            {
                FontFamily = draft.Editor.FontFamily,
                FontSize = draft.Editor.FontSize,
                Bold = draft.Editor.Bold,
                Italic = draft.Editor.Italic
            }
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginApplicationCancellation!.Token);
        pendingOperation = SaveAsync();
        await pendingOperation;

        async Task SaveAsync()
        {
            UpdateProjectActions(busy: true);
            try
            {
                var artwork = await LoadPluginThemeArtworkAsync(pluginId, catalog, style.Theme, cancellation.Token);
                if (artwork is not null)
                {
                    // 实际解码成功后才发布偏好；插件仅提供 PNG 数据，不能执行 XAML。
                    _ = DecodeThemeArtwork(artwork);
                    await services.Themes.Artwork.ImportAsync(style.Theme.Artwork!, artwork, cancellation.Token);
                }
                await services.WorkbenchStyle.SaveAsync(style, cancellation.Token);
                if (closing || closed)
                {
                    return;
                }
                // 外观操作不调用 ApplyBackground，避免重新打开视频或重置播放位置。
                ApplyTheme(style.Theme);
                ApplyTypography(style.Typography);
                ApplyEditorSettings(style.Editor);
                Status.Text = "配色与字体已保存，动态壁纸继续使用原设置。";
            }
            finally
            {
                UpdateProjectActions(busy: false);
            }
        }
    }
}

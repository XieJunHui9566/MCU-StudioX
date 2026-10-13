namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application;
using StudioX.Extensions.Abstractions;

/// <summary>宿主渲染外观目录、已安装字体和本地预览；点击应用后才保存全局偏好。</summary>
internal sealed class PluginAppearanceView : UserControl
{
    private readonly Func<WorkbenchStyle> current;
    private readonly Func<WorkbenchStyle, Task> apply;
    private readonly Action<string> diagnostic;
    private readonly Func<ThemeDefinition, Task<byte[]?>> loadArtwork;
    private readonly Dictionary<string, ImageSource> artworkCache = new(StringComparer.Ordinal);
    private int artworkPreviewGeneration;
    private readonly ComboBox themePicker = new() { Name = "AppearanceTheme", DisplayMemberPath = "DisplayName" };
    private readonly ComboBox uiFont = new() { Name = "AppearanceUiFont", IsTextSearchEnabled = true };
    private readonly ComboBox codeFont = new() { Name = "AppearanceCodeFont", IsTextSearchEnabled = true };
    private readonly Slider uiSize = new() { Name = "AppearanceUiSize", Minimum = 11, Maximum = 18, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Slider codeSize = new() { Name = "AppearanceCodeSize", Minimum = 10, Maximum = 28, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly CheckBox bold = new() { Name = "AppearanceBold", Content = "粗体", Margin = new Thickness(0, 4, 18, 4) };
    private readonly CheckBox italic = new() { Name = "AppearanceItalic", Content = "斜体", Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox showArtwork = new() { Name = "AppearanceIllustration", Content = "显示氛围背景", Margin = new Thickness(0, 4, 0, 4) };
    private readonly Slider artworkOpacity = new() { Name = "AppearanceIllustrationOpacity", Minimum = 0, Maximum = 100, Value = 75, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly Image sampleArtwork = new() { Name = "AppearanceArtworkPreview", Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Border sampleVeil = new() { IsHitTestVisible = false };
    private readonly Border sample = new() { Padding = new Thickness(20), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock sampleUi = new() { Text = "MCU StudioX · 工程 / 编辑 / 调试", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sampleCode = new() { Text = "/* 外观预览 · 字体与配色 */\n#include <stdint.h>\n\nint main(void)\n{\n    return 0;\n}", Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel swatches = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock status = new() { Name = "AppearanceStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private bool ready;

    internal Task PendingApply { get; private set; } = Task.CompletedTask;
    internal Task PendingArtworkPreview { get; private set; } = Task.CompletedTask;

    public PluginAppearanceView(PluginAppearanceDefinition catalog, Func<WorkbenchStyle> current,
        Func<WorkbenchStyle, Task> apply, Action<string> diagnostic, Func<ThemeDefinition, Task<byte[]?>> loadArtwork)
    {
        this.current = current;
        this.apply = apply;
        this.diagnostic = diagnostic;
        this.loadArtwork = loadArtwork;
        var selected = current();
        themePicker.ItemsSource = catalog.Themes.Select(StudioX.Application.Plugins.PluginAppearanceCatalog.ToTheme)
            .Concat(new[] { selected.Theme, ThemeService.Dark, ThemeService.Light })
            .DistinctBy(theme => theme.Id).ToArray();
        var fonts = TextFontCatalog.Families;
        uiFont.ItemsSource = new[] { TextFontCatalog.Recover(selected.Typography).FontFamily }.Concat(fonts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        codeFont.ItemsSource = new[] { selected.Editor.FontFamily, "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono" }
            .Where(TextFontCatalog.IsTextFamily)
            .Concat(fonts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var body = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(Label("选择主题、氛围与字体，查看预览后应用。", true));
        body.Children.Add(Field("主题", themePicker));
        var illustration = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        illustration.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        illustration.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        illustration.Children.Add(showArtwork);
        var strength = SizeField("氛围强度", artworkOpacity, "%");
        Grid.SetColumn(strength, 1);
        illustration.Children.Add(strength);
        body.Children.Add(illustration);
        var columns = new WrapPanel();
        var ui = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 24, 0) };
        ui.Children.Add(Field("界面字体", uiFont));
        ui.Children.Add(SizeField("界面字号", uiSize));
        var editor = new StackPanel { Width = 330 };
        editor.Children.Add(Field("代码字体", codeFont));
        editor.Children.Add(SizeField("代码字号", codeSize));
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        styles.Children.Add(bold);
        styles.Children.Add(italic);
        editor.Children.Add(styles);
        columns.Children.Add(ui);
        columns.Children.Add(editor);
        body.Children.Add(columns);
        var sampleContent = new StackPanel();
        sampleContent.Children.Add(sampleUi);
        sampleContent.Children.Add(sampleCode);
        sampleContent.Children.Add(swatches);
        var sampleLayers = new Grid { MinHeight = 250, ClipToBounds = true };
        sampleLayers.Children.Add(sampleArtwork);
        sampleLayers.Children.Add(sampleVeil);
        sampleLayers.Children.Add(sampleContent);
        sample.Child = sampleLayers;
        body.Children.Add(sample);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Name = "AppearanceApply", Content = "应用外观", Margin = new Thickness(0, 0, 10, 6) };
        save.Click += async (_, _) =>
        {
            PendingApply = SaveAsync();
            await PendingApply;
        };
        var reset = new Button { Name = "AppearanceReset", Content = "恢复默认配色与字体", Margin = new Thickness(0, 0, 10, 6) };
        reset.Click += (_, _) => Load(new(ThemeService.Dark, new(), new()));
        var reload = new Button { Content = "读取当前外观", Margin = new Thickness(0, 0, 0, 6) };
        reload.Click += (_, _) => Load(current());
        actions.Children.Add(save);
        actions.Children.Add(reset);
        actions.Children.Add(reload);
        body.Children.Add(actions);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(status);
        var surface = new Border { Child = body, Padding = new Thickness(16), CornerRadius = new CornerRadius(6) };
        surface.SetResourceReference(Border.BackgroundProperty, "ToolSurface");
        Content = surface;
        themePicker.SelectionChanged += (_, _) =>
        {
            if (ready && themePicker.SelectedItem is ThemeDefinition theme)
            {
                LoadArtworkControls(theme);
            }
            Preview();
        };
        uiFont.SelectionChanged += (_, _) => Preview();
        codeFont.SelectionChanged += (_, _) => Preview();
        uiSize.ValueChanged += (_, _) => Preview();
        codeSize.ValueChanged += (_, _) => Preview();
        bold.Checked += (_, _) => Preview();
        bold.Unchecked += (_, _) => Preview();
        italic.Checked += (_, _) => Preview();
        italic.Unchecked += (_, _) => Preview();
        showArtwork.Checked += (_, _) => Preview();
        showArtwork.Unchecked += (_, _) => Preview();
        artworkOpacity.ValueChanged += (_, _) => Preview();
        Load(selected);
    }

    private void Load(WorkbenchStyle style)
    {
        ready = false;
        style = style with
        {
            Typography = TextFontCatalog.Recover(style.Typography),
            Editor = TextFontCatalog.Recover(style.Editor)
        };
        var themes = ((ThemeDefinition[])themePicker.ItemsSource).ToList();
        if (themes.All(theme => theme.Id != style.Theme.Id))
        {
            themes.Add(style.Theme);
        }
        themePicker.ItemsSource = themes.ToArray();
        themePicker.SelectedItem = themes.First(theme => theme.Id == style.Theme.Id);
        SelectFont(uiFont, style.Typography.FontFamily);
        SelectFont(codeFont, style.Editor.FontFamily);
        uiSize.Value = style.Typography.FontSize;
        codeSize.Value = style.Editor.FontSize;
        bold.IsChecked = style.Editor.Bold;
        italic.IsChecked = style.Editor.Italic;
        LoadArtworkControls(style.Theme);
        ready = true;
        Preview();
    }

    private static void SelectFont(ComboBox picker, string family)
    {
        TextFontCatalog.Validate(family);
        var fonts = ((string[])picker.ItemsSource).ToList();
        if (!fonts.Contains(family, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Insert(0, family);
            picker.ItemsSource = fonts.ToArray();
        }
        picker.SelectedItem = fonts.First(font => font.Equals(family, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadArtworkControls(ThemeDefinition theme)
    {
        var available = ((ThemeDefinition)themePicker.SelectedItem).Artwork is not null;
        showArtwork.IsEnabled = available;
        showArtwork.IsChecked = available && theme.Artwork is not null;
        artworkOpacity.IsEnabled = available;
        artworkOpacity.Value = (theme.Artwork?.Opacity ?? ((ThemeDefinition)themePicker.SelectedItem).Artwork?.Opacity ?? .75) * 100;
    }

    private WorkbenchStyle Draft => new(((ThemeDefinition)themePicker.SelectedItem) with
    {
        Artwork = showArtwork.IsChecked == true && ((ThemeDefinition)themePicker.SelectedItem).Artwork is { } artwork
                ? artwork with
                {
                    Opacity = artworkOpacity.Value / 100
                } : null
    },
        new(1, (string)uiFont.SelectedItem, uiSize.Value), current().Editor with
        {
            FontFamily = (string)codeFont.SelectedItem,
            FontSize = codeSize.Value,
            Bold = bold.IsChecked == true,
            Italic = italic.IsChecked == true
        });

    private void Preview()
    {
        if (!ready)
        {
            return;
        }
        var draft = Draft;
        Brush Color(string key) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(draft.Theme.Colors[key]));
        sample.Background = Color("Background");
        var baseColor = ((SolidColorBrush)Color("Background")).Color;
        sampleVeil.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new(System.Windows.Media.Color.FromArgb(240, baseColor.R, baseColor.G, baseColor.B), 0),
            new(System.Windows.Media.Color.FromArgb(210, baseColor.R, baseColor.G, baseColor.B), .45),
            new(System.Windows.Media.Color.FromArgb(30, baseColor.R, baseColor.G, baseColor.B), 1)
        }, new Point(0, .5), new Point(1, .5));
        sample.BorderBrush = Color("Border");
        sampleUi.Foreground = Color("Text");
        sampleUi.FontFamily = new FontFamily(draft.Typography.FontFamily);
        sampleUi.FontSize = draft.Typography.FontSize;
        sampleCode.Foreground = Color("Accent");
        sampleCode.FontFamily = new FontFamily(draft.Editor.FontFamily + ", Consolas");
        sampleCode.FontSize = draft.Editor.FontSize;
        sampleCode.FontWeight = draft.Editor.Bold ? FontWeights.Bold : FontWeights.Normal;
        sampleCode.FontStyle = draft.Editor.Italic ? FontStyles.Italic : FontStyles.Normal;
        swatches.Children.Clear();
        foreach (var key in new[] { "Surface", "Panel", "Border", "Text", "Muted", "Accent" })
        {
            swatches.Children.Add(new Border { Background = Color(key), Width = 40, Height = 8, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(3), ToolTip = draft.Theme.Colors[key] });
        }
        status.Text = "预览尚未保存 · 动态壁纸、背景透明度与编辑器柔光设置保留。";
        PendingArtworkPreview = PreviewArtworkAsync(draft.Theme, ++artworkPreviewGeneration);
    }

    private async Task PreviewArtworkAsync(ThemeDefinition theme, int generation)
    {
        sampleArtwork.Source = null;
        if (theme.Artwork is null || theme.Artwork.Opacity == 0)
        {
            return;
        }
        try
        {
            if (!artworkCache.TryGetValue(theme.Artwork.Asset, out var image))
            {
                var bytes = await loadArtwork(theme) ?? throw new InvalidDataException("主题没有可用插画。");
                image = MainWindow.DecodeThemeArtwork(bytes);
                artworkCache[theme.Artwork.Asset] = image;
            }
            if (generation == artworkPreviewGeneration)
            {
                sampleArtwork.Source = image;
                sampleArtwork.Opacity = theme.Artwork.Opacity;
            }
        }
        catch (Exception error)
        {
            diagnostic(error.ToString());
            if (generation == artworkPreviewGeneration)
            {
                status.Text = "氛围预览失败：" + error.Message;
            }
        }
    }

    private async Task SaveAsync()
    {
        if (!IsEnabled)
        {
            return;
        }
        var draft = Draft;
        IsEnabled = false;
        try
        {
            await apply(draft);
            status.Text = "已保存配色与字体，下次启动自动恢复。动态壁纸继续使用原设置。";
        }
        catch (Exception error)
        {
            status.Text = "应用失败：" + error.Message;
            diagnostic(error.ToString());
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static StackPanel Field(string text, FrameworkElement input)
    {
        input.Margin = new Thickness(0, 6, 0, 0);
        var group = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        group.Children.Add(Label(text));
        group.Children.Add(input);
        return group;
    }

    private static StackPanel SizeField(string text, Slider slider, string unit = "px")
    {
        var caption = Label(text);
        slider.ValueChanged += (_, _) => caption.Text = $"{text} · {slider.Value:0} {unit}";
        var group = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        group.Children.Add(caption);
        group.Children.Add(slider);
        return group;
    }

    private static TextBlock Label(string text, bool heading = false)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        return label;
    }
}

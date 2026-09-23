namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using StudioX.Application;
using StudioX.Foundation;

public partial class AppearanceWindow : Window
{
    private readonly WorkbenchService services;
    private readonly Action<ThemeDefinition, BackgroundSettings, EditorSettings> preview;
    private readonly List<ThemeDefinition> themes;
    private ThemeDefinition committedTheme;
    private BackgroundSettings committedBackground;
    private EditorSettings committedEditor;
    private BackgroundKind kind;
    private string? asset;
    private bool ready;
    private bool busy;
    private string? mediaError;
    public AppearanceWindow(WorkbenchService services, ThemeDefinition theme, BackgroundSettings background, EditorSettings editor, Action<ThemeDefinition, BackgroundSettings, EditorSettings> preview)
    {
        this.services = services; this.preview = preview;
        committedTheme = theme; committedBackground = background; kind = background.Kind; asset = background.Asset;
        committedEditor = editor;
        themes = [ThemeService.Dark, ThemeService.Light];
        if (!themes.Any(t => t.Id == theme.Id)) themes.Add(theme);
        InitializeComponent();
        ThemePicker.ItemsSource = themes; ThemePicker.SelectedItem = themes.Single(t => t.Id == theme.Id);
        var installed = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FontPicker.ItemsSource = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", editor.FontFamily }.Distinct().Where(installed.Contains).ToArray();
        FontPicker.SelectedItem = editor.FontFamily;
        if (FontPicker.SelectedItem is null) FontPicker.SelectedItem = "Consolas";
        FontSizeSlider.Value = editor.FontSize; GlowCheck.IsChecked = editor.SoftGlow;
        GlowStrengthSlider.Value = editor.GlowStrength;
        OpacitySlider.Value = background.Opacity * 100; DimSlider.Value = background.Dim * 100; BlurSlider.Value = background.Blur;
        FitPicker.SelectedIndex = background.Fit == BackgroundFit.Fill ? 0 : 1; PauseCheck.IsChecked = background.PauseWhenInactive;
        UpdateMediaLabel(); ready = true;
    }
    private BackgroundSettings Draft => new(1, kind, asset, OpacitySlider.Value / 100, DimSlider.Value / 100, BlurSlider.Value,
        FitPicker.SelectedIndex == 1 ? BackgroundFit.Fit : BackgroundFit.Fill, PauseCheck.IsChecked == true);
    private ThemeDefinition SelectedTheme => (ThemeDefinition)ThemePicker.SelectedItem;
    private EditorSettings EditorDraft => new(1, FontPicker.SelectedItem as string ?? "Consolas", Math.Round(FontSizeSlider.Value), GlowCheck.IsChecked == true, Math.Round(GlowStrengthSlider.Value));
    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || ThemePicker.SelectedItem is not ThemeDefinition) return;
        PreviewDraft();
    }
    private bool PreviewDraft()
    {
        try { preview(SelectedTheme, Draft, EditorDraft); ErrorText.Text = mediaError ?? ""; return mediaError is null; }
        catch (Exception ex) { ErrorText.Text = ex.Message; return false; }
    }
    public void ReportMediaError(string message) { mediaError = message; ErrorText.Text = message; }
    private void UpdateMediaLabel(string? name = null)
    {
        MediaName.Text = name ?? (kind == BackgroundKind.None ? "未设置背景" : kind == BackgroundKind.Image ? "已保存的背景图片" : "已保存的背景视频");
        MediaDescription.Text = kind switch { BackgroundKind.Image => "图片 · 即时预览 · 已复制到用户资源目录", BackgroundKind.Video => "视频 · 静音循环 · 已复制到用户资源目录", _ => "默认使用纯色主题背景。" };
    }
    private async void ChooseImage_Click(object sender, RoutedEventArgs e) => await ChooseMediaAsync(BackgroundKind.Image);
    private async void ChooseVideo_Click(object sender, RoutedEventArgs e) => await ChooseMediaAsync(BackgroundKind.Video);
    private async Task ChooseMediaAsync(BackgroundKind selectedKind)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Title = selectedKind == BackgroundKind.Image ? "选择背景图片" : "选择背景视频",
            Filter = selectedKind == BackgroundKind.Image ? "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" : "视频|*.mp4;*.m4v;*.wmv;*.mov" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        var previousKind = kind; var previousAsset = asset;
        try
        {
            var imported = await services.Appearance.ImportAsync(dialog.FileName, selectedKind);
            kind = selectedKind; asset = imported; mediaError = null;
            if (!PreviewDraft()) { kind = previousKind; asset = previousAsset; return; }
            UpdateMediaLabel(Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        kind = BackgroundKind.None; asset = null; mediaError = null; UpdateMediaLabel(); PreviewDraft();
    }
    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "JSON 配色主题|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var theme = await JsonStore.ReadAsync<ThemeDefinition>(dialog.FileName); ThemeService.Validate(theme);
            if (theme.Id is "studiox.dark" or "studiox.light") throw new StudioXException("THEME_RESERVED", "自定义主题需使用自己的 ID。");
            themes.RemoveAll(t => t.Id == theme.Id); themes.Add(theme);
            ThemePicker.ItemsSource = null; ThemePicker.ItemsSource = themes; ThemePicker.SelectedItem = theme;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async Task<bool> CommitAsync()
    {
        if (busy || !PreviewDraft()) return false;
        SetBusy(true);
        var theme = SelectedTheme; var background = Draft; var editor = EditorDraft;
        try
        {
            await services.Appearance.SaveAsync(background);
            await services.Themes.SelectAsync(theme);
            await services.EditorSettings.SaveAsync(editor);
            committedTheme = theme; committedBackground = background; committedEditor = editor; ErrorText.Text = "已保存。";
            return true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; return false; }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value) { busy = value; ApplyButton.IsEnabled = SaveButton.IsEnabled = ThemePicker.IsEnabled = !value; }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await CommitAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) { if (await CommitAsync()) Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!busy) Close(); }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (busy) { e.Cancel = true; return; }
        try { preview(committedTheme, committedBackground, committedEditor); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
}

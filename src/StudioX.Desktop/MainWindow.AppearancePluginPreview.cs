namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离用户目录验证真实插件、字体和本地视频播放，不使用工程或设备。</summary>
    public async Task RenderAppearancePluginPreviewAsync(string directory, string archive, string video)
    {
        Width = 1460;
        Height = 1040;
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Layout()
        {
            FlushPluginPanels();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        Check(!TextFontCatalog.IsTextFamily("AmdtSymbols") && !TextFontCatalog.IsTextFamily("Wingdings") &&
            !TextFontCatalog.IsTextFamily("Webdings") && !TextFontCatalog.IsTextFamily("Segoe MDL2 Assets"),
            "符号标记与实际字符覆盖共同拦截工程符号和图标字体");
        Check(TextFontCatalog.IsTextFamily("Microsoft YaHei UI") && TextFontCatalog.IsTextFamily("Consolas") &&
            TextFontCatalog.IsTextFamily("STSong"), "常用中文与代码文本字体继续可选");
        await services.Typography.SaveAsync(new(1, "AmdtSymbols", 13));
        await services.EditorSettings.SaveAsync(new(1, "Wingdings", 16, true, 76)
        {
            Bold = true,
            Italic = false
        });
        await InitializeAsync(loadGitHubAccounts: false);
        Check(typographySettings.FontFamily == new TypographySettings().FontFamily && FontFamily.Source == typographySettings.FontFamily && FontSize == 13,
            "实际启动恢复修正已保存的 AmdtSymbols 并保留界面字号");
        var recoveredTypography = await services.Typography.LoadAsync();
        var recoveredEditor = await services.EditorSettings.LoadAsync();
        Check(recoveredTypography == typographySettings && recoveredEditor.FontFamily == "Consolas" && recoveredEditor.FontSize == 16 &&
            recoveredEditor.SoftGlow && recoveredEditor.GlowStrength == 76 && recoveredEditor.Bold && !recoveredEditor.Italic,
            "已保存的符号字体修复落盘并保留编辑器字号粗斜体和柔光");
        var originalTypographyBytes = await File.ReadAllBytesAsync(Path.Combine(services.DataDirectory, "typography.json"));
        var installed = await services.PluginManager.ImportAsync(archive);
        Check(!installed.Enabled && installed.Manifest?.Scope == "application", "安装后默认禁用，外观插件不依赖工程");
        await services.PluginManager.SetEnabledAsync(installed.Id, true);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        Check(pluginApplication?.Contributions.Count == 1 && pluginWorkspace is null && pluginActivities.ContainsKey(installed.Id),
            "欢迎页从真实独立宿主加载外观目录与活动入口");
        ShowDocument(pluginActivities[installed.Id].Tab);
        // 使用工程树的真实标签构造入口，复现用户截图中的混合中英文文件名。
        ProjectTree.Items.Clear();
        ProjectTree.Visibility = Visibility.Visible;
        EmptyProject.Visibility = Visibility.Collapsed;
        var tree = new TreeViewItem { Header = FileLabel("my_firmware", true), IsExpanded = true };
        ProjectTree.Items.Add(tree);
        foreach (var name in new[] { ".build", ".studiox", "device · 器件支持", "Hardware", "include", "src", "main.c", "CMakeLists.txt" })
        {
            tree.Items.Add(new TreeViewItem { Header = FileLabel(name, !name.Contains('.')) });
        }
        await Layout();
        var view = PluginDescendants(pluginPanels[installed.Id + "/appearance"].Host).OfType<PluginAppearanceView>().Single();
        T Control<T>(string name) where T : FrameworkElement => PluginDescendants(view).OfType<T>().Single(control => control.Name == name);
        Check(Control<ComboBox>("AppearanceUiFont").Items.Cast<string>().All(TextFontCatalog.IsTextFamily) &&
            Control<ComboBox>("AppearanceCodeFont").Items.Cast<string>().All(TextFontCatalog.IsTextFamily), "界面和代码字体列表只包含可读文本字体");
        foreach (var style in new[]
        {
            new WorkbenchStyle(currentTheme, new(1, "AmdtSymbols", 13), editorSettings),
            new WorkbenchStyle(currentTheme, typographySettings, editorSettings with { FontFamily = "Wingdings" })
        })
        {
            try
            {
                await ApplyPluginAppearanceAsync(installed.Id, pluginApplication, style);
                throw new InvalidOperationException("符号字体被保存到全局偏好");
            }
            catch (StudioXException error) when (error.Code == "TYPOGRAPHY_TEXT_FONT")
            {
                Check(true, "保存入口拒绝符号字体且不改写全局外观");
            }
        }
        var themePicker = Control<ComboBox>("AppearanceTheme");
        var originalId = currentTheme.Id;
        themePicker.SelectedItem = themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance.hina-night");
        await view.PendingArtworkPreview;
        Check(Control<Image>("AppearanceArtworkPreview").Source is not null && currentTheme.Id == originalId &&
            !Directory.Exists(Path.Combine(services.DataDirectory, "theme-artwork")), "氛围环境在面板内实际解码预览且不发布用户主题资源或偏好");
        var atmosphere = themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance.hina-night");
        try
        {
            await services.PluginManager.LoadAppearanceArtworkAsync(installed.Id,
                new("assets/not-indexed.png", new string('a', 64)));
            throw new InvalidOperationException("未索引插画被接受");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_ARTWORK_UNTRUSTED")
        {
            Check(true, "氛围资源必须来自当前已启用插件的完整哈希索引");
        }
        themePicker.SelectedItem = themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance.ocean");
        Control<ComboBox>("AppearanceUiFont").SelectedItem = "Microsoft YaHei UI";
        Control<ComboBox>("AppearanceCodeFont").SelectedItem = "Consolas";
        Control<Slider>("AppearanceUiSize").Value = 15;
        Control<Slider>("AppearanceCodeSize").Value = 18;
        Control<CheckBox>("AppearanceBold").IsChecked = true;
        Control<CheckBox>("AppearanceItalic").IsChecked = true;
        var previewTypographyBytes = await File.ReadAllBytesAsync(Path.Combine(services.DataDirectory, "typography.json"));
        Check(currentTheme.Id == originalId && originalTypographyBytes.SequenceEqual(previewTypographyBytes),
            "面板预览不提前改写工作台或偏好");
        var editorBefore = editorSettings with
        {
            SoftGlow = false,
            GlowStrength = 37
        };
        ApplyEditorSettings(editorBefore);
        await services.EditorSettings.SaveAsync(editorBefore);
        var framesOpened = 0;
        BackgroundVideo.MediaOpened += (_, _) => framesOpened++;
        var mediaAsset = await services.Appearance.ImportAsync(video, BackgroundKind.Video);
        var background = new BackgroundSettings(1, BackgroundKind.Video, mediaAsset, .65, .15, 2, BackgroundFit.Fit, false, false);
        await services.Appearance.SaveAsync(background);
        ApplyBackground(background);
        for (var attempt = 0; attempt < 150 && !videoReady; attempt++)
        {
            await Task.Delay(100);
        }
        Check(videoReady && BackgroundVideo.NaturalDuration.HasTimeSpan && framesOpened == 1, "实际本地视频通过 MediaOpened 并取得时长");
        await Task.Delay(1000);
        Check(BackgroundVideo.Position > TimeSpan.Zero, "视频壁纸实际推进播放位置");
        var mediaSource = BackgroundVideo.Source;
        var mediaPreferences = await File.ReadAllBytesAsync(Path.Combine(services.DataDirectory, "appearance.json"));
        var codeBuffer = "int unchanged(void) { return 7; }\n";
        SourceEditor.Text = codeBuffer;
        async Task Apply()
        {
            Control<Button>("AppearanceApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.PendingApply;
            Check(Control<TextBlock>("AppearanceStatus").Text.StartsWith("已保存", StringComparison.Ordinal), "真实外观按钮保存成功");
            await Layout();
        }
        await Apply();
        Check(currentTheme.Id == "studiox.appearance.ocean" && FontSize == 15 && FontFamily.Source == "Microsoft YaHei UI", "真实工作台配色与界面字体即时更新");
        var treeLabels = tree.Items.Cast<TreeViewItem>().SelectMany(item => ((StackPanel)item.Header).Children.OfType<TextBlock>()).ToArray();
        Check(ProjectTree.IsVisible && treeLabels.Length == 8 && treeLabels.All(label => label.IsVisible &&
            TextFontCatalog.IsTextFamily(label.FontFamily.Source)), "真实可见工程树的中英文标签继承可读界面字体");
        Check(SourceEditor.FontFamily.Source.StartsWith("Consolas", StringComparison.Ordinal) && SourceEditor.FontSize == 18 &&
            SourceEditor.FontWeight == FontWeights.Bold && SourceEditor.FontStyle == FontStyles.Italic, "代码字体字号粗斜体实际生效");
        Check(SourceEditor.Text == codeBuffer && !editorSettings.SoftGlow && editorSettings.GlowStrength == 37,
            "配色字体不改变编辑文本与柔光偏好");
        Check(BackgroundVideo.Source == mediaSource && videoReady && framesOpened == 1 && backgroundSettings == background,
            "切换外观没有重新打开视频或改写背景设置");
        Check(((SolidColorBrush)System.Windows.Application.Current.Resources["EditorSurface"]).Color.A == 100 &&
            ((SolidColorBrush)System.Windows.Application.Current.Resources["ToolSurface"]).Color.A == 195, "新主题保持动态壁纸的半透明工作区");
        await Task.Delay(700);
        Check(BackgroundVideo.Position > TimeSpan.Zero && framesOpened == 1, "应用字体和配色后视频继续播放");
        Render(this, Path.Combine(directory, "ocean-video.png"));
        themePicker.SelectedItem = themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance.cream");
        await Apply();
        Check(((SolidColorBrush)System.Windows.Application.Current.Resources["Background"]).Color == (Color)ColorConverter.ConvertFromString("#FBF6EA"),
            "暖纸浅色主题使用插件提供的语义颜色");
        var savedMediaPreferences = await File.ReadAllBytesAsync(Path.Combine(services.DataDirectory, "appearance.json"));
        Check(mediaPreferences.SequenceEqual(savedMediaPreferences) && framesOpened == 1,
            "连续切换深浅主题后壁纸偏好字节一致且媒体只打开一次");
        Render(this, Path.Combine(directory, "cream-video.png"));
        foreach (var id in new[] { "hina-night", "hina-moon", "hina-dusk" })
        {
            themePicker.SelectedItem = themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance." + id);
            await view.PendingArtworkPreview;
            await Apply();
            var atmosphereMediaPreferences = await File.ReadAllBytesAsync(Path.Combine(services.DataDirectory, "appearance.json"));
            Check(ThemeArtworkImage.Source is not null && ThemeArtworkImage.IsVisible && !ThemeArtworkImage.IsHitTestVisible &&
                Math.Abs(ThemeArtworkImage.Opacity - currentTheme.Artwork!.Opacity * .3) < .001 &&
                BackgroundVideo.Source == mediaSource && videoReady && framesOpened == 1 &&
                mediaPreferences.SequenceEqual(atmosphereMediaPreferences),
                id + " 氛围叠加保留视频会话、暂停规则及壁纸偏好");
            Render(this, Path.Combine(directory, id + "-video.png"));
        }
        Control<CheckBox>("AppearanceIllustration").IsChecked = false;
        await Apply();
        Check(ThemeArtworkImage.Source is null && currentTheme.Artwork is null && framesOpened == 1,
            "关闭氛围只移除主题图层，视频继续使用同一会话");
        Control<CheckBox>("AppearanceIllustration").IsChecked = true;
        Control<Slider>("AppearanceIllustrationOpacity").Value = 55;
        await Apply();
        Check(currentTheme.Artwork?.Opacity == .55 && Math.Abs(ThemeArtworkImage.Opacity - .165) < .001,
            "氛围强度可以调整并独立保存");
        WindowState = WindowState.Minimized;
        await Task.Delay(400);
        var paused = BackgroundVideo.Position;
        await Task.Delay(400);
        Check(Math.Abs((BackgroundVideo.Position - paused).TotalMilliseconds) < 100, "最小化继续遵循视频暂停规则");
        WindowState = WindowState.Normal;
        await Task.Delay(400);
        Check(BackgroundVideo.Position != paused, "恢复窗口后视频继续播放");
        var ended = 0;
        BackgroundVideo.MediaEnded += (_, _) => ended++;
        BackgroundVideo.Position = BackgroundVideo.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(300);
        for (var attempt = 0; attempt < 30 && ended == 0; attempt++)
        {
            await Task.Delay(100);
        }
        await Task.Delay(300);
        Check(ended > 0 && BackgroundVideo.Position > TimeSpan.Zero && BackgroundVideo.Position < TimeSpan.FromSeconds(2) && framesOpened == 1,
            "主题切换后视频到结尾继续循环且不重新打开媒体");
        ApplyBackground(background with
        {
            PauseWhenInactive = true,
            OpaqueWorkSurfaces = true
        });
        await Task.Delay(300);
        var inactivePosition = BackgroundVideo.Position;
        await Task.Delay(300);
        Check(!IsActive && Math.Abs((BackgroundVideo.Position - inactivePosition).TotalMilliseconds) < 100,
            "失焦暂停选项继续生效");
        await Apply();
        Check(((SolidColorBrush)System.Windows.Application.Current.Resources["EditorSurface"]).Color.A == 255 && framesOpened == 1,
            "外观应用保留不透明工作区选项与媒体会话");
        ApplyBackground(background);
        var reopenedTheme = await new ThemeService(services.DataDirectory).LoadAsync();
        var reopenedEditor = await new EditorSettingsService(services.DataDirectory).LoadAsync();
        var reopenedTypography = await new TypographyService(services.DataDirectory).LoadAsync();
        Check(reopenedTheme.Id == currentTheme.Id && reopenedEditor == editorSettings && reopenedTypography == typographySettings,
            "新的偏好服务实例恢复完整配色与字体");
        Check(reopenedTheme.Artwork == currentTheme.Artwork &&
            services.Themes.Artwork.Read(reopenedTheme.Artwork!).Length > 0, "主题环境副本和强度支持重启恢复");
        // 同面板重复发布保留用户的未应用选择，且不会创建第二个媒体会话。
        Control<Slider>("AppearanceCodeSize").Value = 19;
        UpdatePluginPanel(installed.Id + "/appearance", pluginApplication!.LatestPanels[installed.Id + "/appearance"]);
        await Layout();
        Check(PluginDescendants(pluginPanels[installed.Id + "/appearance"].Host).OfType<PluginAppearanceView>().Single() == view &&
            Control<Slider>("AppearanceCodeSize").Value == 19, "面板刷新保留未应用输入");
        await services.PluginManager.SetEnabledAsync(installed.Id, false);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        Check(!pluginActivities.ContainsKey(installed.Id) && currentTheme.Id == reopenedTheme.Id && framesOpened == 1,
            "禁用插件移除入口并保留已选外观和视频播放");
        try
        {
            await ApplyPluginAppearanceAsync(installed.Id, null, new(ThemeService.Dark, new(), new()));
            throw new InvalidOperationException("已停用插件接受外观操作");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_INACTIVE")
        {
            Check(true, "迟到外观操作不能在插件禁用后应用");
        }
        ApplyBackground(new BackgroundSettings());
        foreach (var id in new[] { "hina-night", "hina-moon", "hina-dusk" })
        {
            ApplyTheme(themePicker.Items.Cast<ThemeDefinition>().Single(theme => theme.Id == "studiox.appearance." + id));
            await Layout();
            Check(ThemeArtworkImage.IsVisible && loadedMediaPath is null &&
                Math.Abs(ThemeArtworkImage.Opacity - currentTheme.Artwork!.Opacity) < .001 &&
                ((SolidColorBrush)System.Windows.Application.Current.Resources["EditorSurface"]).Color.A == 170,
                id + " 无壁纸时显示完整氛围环境并保留可读编辑底色");
            Render(this, Path.Combine(directory, id + "-environment.png"));
        }
        var cachedArtwork = Path.Combine(services.DataDirectory, "theme-artwork", atmosphere.Artwork!.Asset);
        var cachedBytes = await File.ReadAllBytesAsync(cachedArtwork);
        await File.WriteAllBytesAsync(cachedArtwork, [1, 2, 3]);
        loadedThemeArtwork = null;
        ApplyTheme(atmosphere);
        Check(ThemeArtworkImage.Source is null && currentTheme.Id == atmosphere.Id,
            "损坏氛围资源退回可读配色并记录原始诊断");
        await File.WriteAllBytesAsync(cachedArtwork, cachedBytes);
        ApplyTheme(atmosphere);
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = true,
            hardware = false,
            mediaPlaybackTested = true,
            checks
        });
        await File.WriteAllLinesAsync(Path.Combine(directory, "result.txt"), checks);
    }
}

namespace StudioX.Desktop;

using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>只改小型工程副本，验证真实 WPF 选择和持久化；不运行 SDK 或访问串口。</summary>
    public async Task RenderEspressifModulePreviewAsync(string directory, string project)
    {
        var fixtures = Path.Combine(directory, "fixtures");
        if (Directory.Exists(fixtures))
        {
            throw new InvalidOperationException("模块检查需要新的输出目录。");
        }
        var sourceProjects = Path.GetDirectoryName(project)!;
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceRoots = new Dictionary<string, string>(StringComparer.Ordinal);
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { "esp32s3", "esp32p4", "esp32", "esp32c3", "esp32c5", "esp32c6", "esp8266" })
        {
            var source = Path.Combine(sourceProjects, target + "_hello_world");
            if (target == "esp8266" && !Directory.Exists(source))
            {
                source = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(sourceProjects))!,
                    "espressif-projects-current", "projects", "esp8266_hello_world");
            }
            Check(Directory.Exists(source), "缺少离线工程：" + source);
            var destination = Path.Combine(fixtures, target);
            CopyModuleFixture(source, destination, originals);
            roots[target] = destination;
            sourceRoots[target] = source;
        }
        try
        {
            Width = 1460;
            Height = 1020;
            var s3 = roots["esp32s3"];
            await RunAsync(token => OpenProjectAsync(s3, token));
            await ShowProjectDetailsAsync();
            Check(EspressifModulePanel.Visibility == Visibility.Visible && EspressifModuleEditor.IsEnabled &&
                EspressifModuleProfilePicker.SelectedValue is "inherit" && !SaveEspressifModuleButton.IsEnabled,
                "新 ESP 工程未默认沿用 sdkconfig，或模块卡片不可用。");
            var n8 = espressifModuleProfiles.Single(profile => profile.Label.Contains("WROOM-1-N8R8", StringComparison.Ordinal));
            var n16 = espressifModuleProfiles.Single(profile => profile.Label.Contains("WROOM-1-N16R8", StringComparison.Ordinal));
            await ChooseProfile(n8.Id);
            Check(TrySelectedEspressifModule(out var selected, out _) && selected == n8.Settings &&
                !EspressifModuleParameters.IsEnabled && SaveEspressifModuleButton.IsEnabled &&
                !File.Exists(Path.Combine(s3, EspressifModuleSettings.RelativePath)),
                "预设没有预填/锁定真实参数，或选择即擅自落盘。");
            await Capture("s3-n8r8");
            await SaveSelected();
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == n8.Settings && !SaveEspressifModuleButton.IsEnabled,
                "N8R8 配置没有通过应用服务保存。");
            await OpenSourceAsync(EspressifModuleSettings.RelativePath, CancellationToken.None);
            var settingsEditor = activeEditor!;
            var oldText = settingsEditor.Buffer.Text;
            await ShowProjectDetailsAsync();
            Check(EspressifModuleProfilePicker.SelectedValue is string savedId && savedId == n8.Id,
                "重开详情丢失已保存的模组。");
            await ChooseProfile(n16.Id);
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == n8.Settings,
                "改变预设在点击保存之前修改了工程。");
            await SaveSelected();
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == n16.Settings &&
                settingsEditor.Buffer.Text != oldText && !settingsEditor.IsDirty &&
                JsonDocument.Parse(settingsEditor.Buffer.Text).RootElement.GetProperty("flashSizeMb").GetInt32() == 16,
                "保存 N16R8 后编辑区没有实时更新侧车内容。");
            Check(((TextBlock)BuildMemory.FindName("AnalysisStatus")).Text.Contains("重新编译", StringComparison.Ordinal),
                "模组保存后仍显示旧构建资源。");
            await Capture("s3-n16r8");
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                BuildSettingsEditor.BringIntoView();
                await Layout();
                Check(BuildSettingsEditor.IsVisible && BuildSettingsEditor.ActualHeight > 0 && SaveBuildSettingsButton.IsVisible,
                    "新增模块卡片遮挡了下方编译参数，或编译参数无法滚动到达。");
                Render(this, Path.Combine(directory, "compile-settings-" + theme.Id + ".png"));
            }
            Width = 1120;
            await Layout();
            EspressifModulePanel.BringIntoView();
            await Layout();
            Render(this, Path.Combine(directory, "s3-module-narrow.png"));
            Width = 1460;
            await ChooseProfile("custom");
            SelectModuleValue<int?>(EspressifFlashSizePicker, 8);
            SelectModuleValue<string?>(EspressifFlashModePicker, "dio");
            SelectModuleValue<int?>(EspressifFlashFrequencyPicker, 40);
            SelectModuleValue<string?>(EspressifPsramModePicker, "quad");
            EspressifPsramSizeBox.Text = "4";
            Check(EspressifModuleParameters.IsEnabled && SaveEspressifModuleButton.IsEnabled &&
                TrySelectedEspressifModule(out var custom, out _) && custom.ProfileId is null && custom.PsramSizeMb == 4,
                "自定义无法调整合法 Flash / PSRAM 参数。");
            await Capture("s3-custom");
            EspressifPsramSizeBox.Text = "1";
            Check(!SaveEspressifModuleButton.IsEnabled && EspressifModuleStatus.Text.Contains("PSRAM", StringComparison.Ordinal) &&
                await services.Builds.LoadEspressifModuleSettingsAsync(s3) == n16.Settings,
                "无效自定义容量未阻止保存，或改写了已保存设置。");
            CancelEspressifModuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(TrySelectedEspressifModule(out selected, out _) && selected == n16.Settings && !SaveEspressifModuleButton.IsEnabled,
                "撤销未保存没有恢复已保存模组参数。");
            await ChooseProfile("custom");
            SelectModuleValue<int?>(EspressifFlashSizePicker, 8);
            SelectModuleValue<string?>(EspressifPsramModePicker, "quad");
            EspressifPsramSizeBox.Text = "4";
            Check(TrySelectedEspressifModule(out custom, out _), "自定义参数无效。");
            await SaveSelected();
            await ShowProjectDetailsAsync();
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == custom &&
                EspressifModuleProfilePicker.SelectedValue is "custom", "自定义参数保存 / 重载不一致。");
            await ChooseProfile("inherit");
            Check(SaveEspressifModuleButton.IsEnabled, "恢复沿用选项没有待保存状态。");
            UpdateProjectActions(true);
            Check(!EspressifModuleEditor.IsEnabled && !SaveEspressifModuleButton.IsEnabled && !CancelEspressifModuleButton.IsEnabled,
                "忙碌期间模块配置仍可修改。");
            SaveEspressifModuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == custom,
                "忙碌期间执行了模块保存。");
            UpdateProjectActions(false);
            await SaveSelected();
            Check(await services.Builds.LoadEspressifModuleSettingsAsync(s3) == new EspressifModuleSettings(),
                "恢复沿用 sdkconfig 后没有清除自定义覆盖。");
            await Capture("s3-inherit");
            var opi = espressifModuleProfiles.First(profile => profile.Settings.FlashMode == "opi");
            await ChooseProfile(opi.Id);
            Check(TrySelectedEspressifModule(out selected, out _) && selected.FlashMode == "opi" &&
                ModuleValue<string?>(EspressifFlashModePicker) == "opi" && selected.PsramMode == opi.Settings.PsramMode,
                "Octal Flash 模组没有区别于 Quad Flash + Octal PSRAM。");
            await Capture("s3-octal-flash");
            CancelEspressifModuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var manifest = currentProjectManifest!;
            var oldRead = LoadEspressifModuleAsync(s3, projectDetailsRevision);
            SetProjectDetailsMode(null);
            await oldRead;
            Check(EspressifModulePanel.Visibility == Visibility.Collapsed && loadedEspressifModule is null,
                "过期读取在工程关闭后回填了模块设置。");
            SetProjectDetailsMode(manifest);
            await ShowProjectDetailsAsync();
            foreach (var target in roots.Keys.Where(target => target != "esp32s3"))
            {
                await RunAsync(token => OpenProjectAsync(roots[target], token));
                await ShowProjectDetailsAsync();
                var capabilities = espressifModuleCapabilities!;
                Check(EspressifModulePanel.Visibility == Visibility.Visible && loadedEspressifModule == new EspressifModuleSettings() &&
                    capabilities.Target == target && EspressifFlashModePicker.Items.Count == capabilities.FlashModes.Length + 1 &&
                    EspressifPsramModePicker.Items.Count == capabilities.PsramModes.Length + 1,
                    target + " 没有通过目标能力构建独立选项，或继承了前一工程的设置。");
                foreach (var profile in new[] { espressifModuleProfiles.First(), espressifModuleProfiles.Last() }.Distinct())
                {
                    await ChooseProfile(profile.Id);
                    Check(TrySelectedEspressifModule(out selected, out _) && selected == profile.Settings &&
                        !EspressifModuleParameters.IsEnabled, target + " 模组预设参数或锁定状态错误。");
                }
                await ChooseProfile("custom");
                Check(EspressifRevisionPanel.Visibility == ((capabilities.P4RevisionFamilies?.Length ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed) &&
                    EspressifCorePanel.Visibility == (capabilities.SupportsSingleCore ? Visibility.Visible : Visibility.Collapsed),
                    target + " 芯片版本 / 核心选项未跟随 SDK 能力。");
                SelectModuleValue<int?>(EspressifFlashSizePicker, 4);
                SelectModuleValue<string?>(EspressifFlashModePicker, "dio");
                SelectModuleValue<int?>(EspressifFlashFrequencyPicker, 40);
                var psram = capabilities.PsramModes.FirstOrDefault(mode => mode != "disabled") ?? "disabled";
                SelectModuleValue<string?>(EspressifPsramModePicker, psram);
                if (psram != "disabled")
                {
                    EspressifPsramSizeBox.Text = "8";
                }
                if (capabilities.P4RevisionFamilies is { Length: > 0 })
                {
                    SelectModuleValue<string?>(EspressifRevisionPicker, "current");
                }
                if (capabilities.SupportsSingleCore)
                {
                    SelectModuleValue<bool?>(EspressifCorePicker, true);
                }
                Check(TrySelectedEspressifModule(out custom, out _) && SaveEspressifModuleButton.IsEnabled,
                    target + " 自定义选择不可保存。");
                await SaveSelected();
                await ShowProjectDetailsAsync();
                Check(await services.Builds.LoadEspressifModuleSettingsAsync(roots[target]) == custom,
                    target + " 自定义选择未经过后端保存 / 重载。");
                await Capture(target + "-custom");
            }
            Check(originals.All(entry => FileHash(entry.Key) == entry.Value), "模块预览修改了原始工程文件。");
            foreach (var (target, root) in roots)
            {
                Check(!Directory.Exists(Path.Combine(root, ".build")), "模块预览运行 SDK 或创建了构建产物。");
                foreach (var name in new[] { "sdkconfig", "sdkconfig.defaults" })
                {
                    var original = Path.Combine(sourceRoots[target], name);
                    if (File.Exists(original))
                    {
                        Check(FileHash(Path.Combine(root, name)) == originals[original], target + " 的原生 " + name + " 被保存动作改写。");
                    }
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
                "PASS: real WPF module selection, S3 N8R8/N16R8 preset locking, save/reload/cancel/default, custom validation, live sidecar editor refresh, stale-read and busy guards; all seven targets use backend capabilities. Dark/light screenshots. Original sources / sdkconfig unchanged; no SDK execution or hardware.\n");
        }
        finally { await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.No); }

        static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        async Task ChooseProfile(string id)
        {
            EspressifModulePanel.BringIntoView();
            await Layout();
            var choice = EspressifModuleProfilePicker.Items.OfType<EspressifModuleChoice>().Single(item => item.Id == id);
            EspressifModuleProfilePicker.SelectedItem = choice;
            EspressifModuleProfilePicker.IsDropDownOpen = false;
            await Layout();
            Check(VisualModuleDescendants(EspressifModuleProfilePicker).OfType<TextBlock>().Any(text =>
                text.IsVisible && text.ActualWidth > 0 && text.Text == choice.Label),
                "WPF 选中模组型号未显示在实际输入框：" + choice.Label);
        }
        async Task SaveSelected()
        {
            Check(SaveEspressifModuleButton.IsEnabled, "模块保存按钮未启用：" + EspressifModuleStatus.Text);
            SaveEspressifModuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pendingOperation;
            Check(!SaveEspressifModuleButton.IsEnabled && EspressifModuleStatus.Text.Contains("已保存", StringComparison.Ordinal),
                "模块保存失败：" + Status.Text);
        }
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        async Task Capture(string name)
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                EspressifModulePanel.BringIntoView();
                await Layout();
                Render(this, Path.Combine(directory, name + "-" + theme.Id + ".png"));
            }
        }
    }

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static IEnumerable<DependencyObject> VisualModuleDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualModuleDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void CopyModuleFixture(string source, string destination, Dictionary<string, string> originals)
    {
        long bytes = 0;
        void Copy(string relative)
        {
            var input = Path.Combine(source, relative);
            if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("模块预览副本拒绝目录链接。");
            }
            if (Directory.Exists(input))
            {
                if (Path.GetFileName(input) is ".build" or ".git" or ".pio")
                {
                    return;
                }
                Directory.CreateDirectory(Path.Combine(destination, relative));
                foreach (var entry in Directory.EnumerateFileSystemEntries(input))
                {
                    Copy(Path.GetRelativePath(source, entry));
                }
                return;
            }
            bytes += new FileInfo(input).Length;
            if (bytes > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException("模块预览只接受小型 SDK 示例副本。");
            }
            originals[input] = FileHash(input);
            File.Copy(input, Path.Combine(destination, relative));
        }
        Copy("");
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>从真实调试点击入口检查编译草稿保护；隔离工程不包含可构建固件或串口配置。</summary>
    public async Task RenderMon51SettingsPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                File.WriteAllText(Path.Combine(directory, "failed-state.txt"),
                    $"{message}\nDetails={ProjectDetailsPanel.Visibility}\nSelected={PackagesTab.IsSelected}\nBuildStatus={BuildSettingsStatus.Text}\nStatus={Status.Text}\nLog={BuildLog.Text}");
                Render(this, Path.Combine(directory, "failed.png"));
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "mon51-settings-custom",
            Colors = new(ThemeService.Dark.Colors)
        };
        custom.Colors["Accent"] = "#B8E986";
        custom.Colors["OnAccent"] = "#163020";
        var saved = new ProjectBuildSettings(Optimization: CompilerOptimization.O0, DebugInfo: CompilerDebugInfo.None, CodeRomSizeBytes: 60000);
        try
        {
            foreach (var device in StcMon51Targets.DeviceIds)
            {
                var root = Path.Combine(directory, device);
                Directory.CreateDirectory(Path.Combine(root, ".studiox"));
                currentProjectManifest = new(1, "Mon51SettingsPreview", "stc.mon51", "1.0.0", "", device, "bare-metal", "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242");
                await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), currentProjectManifest);
                projectDirectory = root;
                ConfigureBuildSettingsForProject();
                ConfigureStcIspForProject();
                stcCodeRomLimit = new(62464, 61952);
                stcRamLimits = new(256, 1, 2047);
                ApplyBuildSettings(saved);
                var buildPath = Path.Combine(root, ProjectBuildSettings.RelativePath);
                await JsonStore.WriteAsync(buildPath, saved);
                var buildBytes = await File.ReadAllBytesAsync(buildPath);
                var sourceName = "pending-" + device + ".c";
                var sourcePath = Path.Combine(root, sourceName);
                await File.WriteAllTextAsync(sourcePath, "void main(void)\n{\n    while (1) {}\n}\n");
                await OpenSourceAsync(sourceName, CancellationToken.None);
                var editor = activeEditor!;
                editor.Buffer.Insert(0, "// 未保存源码也不得在拒绝启动后自动保存。\n");
                var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
                CurrentProjectName.Text = "Mon51SettingsPreview";
                CurrentProjectDeviceId.Text = device;
                var drafts = new (string Name, Action Edit, Func<bool> Preserved, string Message)[]
                {
                    ("optimization", () => BuildOptimizationPicker.SelectedValue = CompilerOptimization.Os,
                        () => SelectedBuildSettings.Optimization == CompilerOptimization.Os, "请先保存后再调试"),
                    ("debug-info", () => BuildDebugInfoPicker.SelectedValue = CompilerDebugInfo.Standard,
                        () => SelectedBuildSettings.DebugInfo == CompilerDebugInfo.Standard, "请先保存后再调试"),
                    ("memory-model", () => SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.Large,
                        () => SelectedBuildSettings.MemoryModel == SdccMemoryModel.Large, "请先保存后再调试"),
                    ("idata-size", () => SdccIdataSizeBox.Text = "192",
                        () => SelectedBuildSettings.IdataSizeBytes == 192, "请先保存后再调试"),
                    ("xdata-size", () => SdccXdataSizeBox.Text = "1024",
                        () => SelectedBuildSettings.XdataSizeBytes == 1024, "请先保存后再调试"),
                    ("rom-size", () => StcCodeRomSizeBox.Text = "48000",
                        () => StcCodeRomSizeBox.Text == "48000", "请先保存后再调试"),
                    ("profile", () => { StcCodeRomSizeBox.Text = "56000"; StcMon51Profile.IsChecked = true; },
                        () => SelectedBuildSettings is { Mon51Profile: true, DebugInfo: CompilerDebugInfo.Standard }, "请先保存后再调试"),
                    ("invalid-rom", () => StcCodeRomSizeBox.Text = "invalid",
                        () => StcCodeRomSizeBox.Text == "invalid", "请输入正整数字节数"),
                    ("oversize-rom", () => StcCodeRomSizeBox.Text = "65536",
                        () => StcCodeRomSizeBox.Text == "65536", "1024–61952")
                };
                foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
                {
                    ApplyTheme(theme);
                    System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                    ConfigureStcIspForProject();
                    if (theme != ThemeService.Dark)
                    {
                        ApplyStcIspSettings(new(Port: "COM14", TransferBaud: 57600),
                            new(device, [StcClockMode.Preserve], false, 0, 0, ""),
                            new(false, null, null, "离线检查，不连接串口"), ["COM14", "COM99"]);
                        if (theme == custom)
                        {
                            StcPortPicker.SelectedItem = "COM99";
                        }
                    }
                    foreach (var draft in drafts)
                    {
                        ApplyBuildSettings(saved);
                        draft.Edit();
                        var portDraft = StcPortPicker.SelectedItem;
                        ShowDocument(editor.Tab);
                        DebugStart_Click(DebugStartButton, new RoutedEventArgs());
                        await pendingOperation;
                        var launchStatus = Status.Text;
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        UpdateLayout();
                        var currentSource = await File.ReadAllBytesAsync(sourcePath);
                        var currentBuild = await File.ReadAllBytesAsync(buildPath);
                        var label = device + " / " + theme.Id + " / " + draft.Name;
                        Check(draft.Preserved() && loadedBuildSettings == saved && Equals(portDraft, StcPortPicker.SelectedItem),
                            label + ": build/port drafts and saved baseline preserved");
                        Check((PackagesTab.IsSelected || StcDownloadTab.IsSelected) &&
                            BuildSettingsStatus.Text.Contains(draft.Message) && launchStatus == BuildSettingsStatus.Text,
                            label + ": debug click locates build settings with inline status");
                        var visibleStatus = StcDownloadTab.IsSelected ? StcBuildSettingsStatus : BuildSettingsStatus;
                        var statusBounds = visibleStatus.TransformToAncestor(WorkspaceTabs).TransformBounds(new Rect(visibleStatus.RenderSize));
                        Check(statusBounds.Top >= 0 && statusBounds.Bottom <= WorkspaceTabs.ActualHeight,
                            label + ": save guidance scrolled into the visible workspace");
                        Check(editor.IsDirty && sourceBytes.AsSpan().SequenceEqual(currentSource) &&
                            buildBytes.AsSpan().SequenceEqual(currentBuild) &&
                            files.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()),
                            label + ": no source save, preset rewrite, receipt invalidation or build output");
                        Check(!services.Debugger.IsActive && !projectActionsBusy && BuildSettingsEditor.IsEnabled &&
                            SaveBuildSettingsButton.IsEnabled == !draft.Name.EndsWith("rom", StringComparison.Ordinal),
                            label + ": launch stopped and form remains editable");
                        if (draft.Name == "rom-size")
                        {
                            Render(this, Path.Combine(directory, device + "-unsaved-" + theme.Id + ".png"));
                        }
                    }
                }
                editor.Buffer.Text = editor.Source.Text;
            }
            await CheckMon51AvailabilityUiAsync(directory, checks);
        }
        finally
        {
            // 失败时也释放隔离预览，不让故意保留的源码草稿阻塞无交互退出。
            foreach (var editor in editorDocuments)
            {
                editor.Buffer.Text = editor.Source.Text;
            }
            ApplyBuildSettings(saved);
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count} WPF Mon51 unsaved-settings checks. Isolated projects; no hardware or serial access.\n" + string.Join('\n', checks));
    }
}

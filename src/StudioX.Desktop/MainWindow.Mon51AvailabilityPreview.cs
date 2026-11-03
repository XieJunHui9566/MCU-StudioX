namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task CheckMon51AvailabilityUiAsync(string directory, List<string> checks)
    {
        var original = currentProjectManifest!;
        var originalDownload = supportsDownload;
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "mon51-availability-custom",
            Colors = new(ThemeService.Dark.Colors)
        };
        custom.Colors["Accent"] = "#B8E986";
        custom.Colors["OnAccent"] = "#163020";
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        var root = RequireProject();
        var source = activeEditor!;
        source.Buffer.Insert(0, "// 不支持调试时不得自动保存这份隔离草稿。\n");
        ApplyBuildSettings(loadedBuildSettings!);
        BuildOptimizationPicker.SelectedValue = CompilerOptimization.Os;
        var draft = SelectedBuildSettings;
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var contents = await Task.WhenAll(files.Select(path => File.ReadAllBytesAsync(path)));
        try
        {
            supportsDownload = true;
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                // 支持型号前后都穿插反例，覆盖从可用工程切换到不支持工程及切回时的状态刷新。
                foreach (var device in new[] { "IAP15F2K61S2", "STC89C52RC", "STC15F2K60S2", "STC8H8K64U", "STC32G12K128", "IAP15L2K61S2", "AI8051U-34K64", "IAP15F2K61S2-extra", "UNKNOWN", "" })
                {
                    currentProjectManifest = original with
                    {
                        DeviceId = device
                    };
                    CurrentProjectDeviceId.Text = device;
                    ShowDocument(source.Tab);
                    UpdateProjectActions(busy: false);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    UpdateLayout();
                    var supported = device is "IAP15F2K61S2" or "IAP15L2K61S2";
                    var label = theme.Id + " / " + device;
                    Check(ProjectCapabilities.For(currentProjectManifest).Debug == supported &&
                        DebugStartButton.Visibility == Visibility.Visible && DebugStartButton.IsEnabled == supported &&
                        DebugStartMenu.IsEnabled == supported && BuildButton.IsEnabled && DownloadButton.IsEnabled,
                        label + ": toolbar/menu follow exact target capability without disabling build or ISP");
                    Check(debugMargin!.Visibility == (supported ? Visibility.Visible : Visibility.Collapsed) &&
                        CanEditBreakpoints == supported && debugMargin.CanEdit == supported &&
                        DebugSourceBreakpointMenu.IsEnabled == supported && DebugSourceEnableMenu.IsEnabled == supported &&
                        DebugSourceSettingsMenu.IsEnabled == supported,
                        label + ": source breakpoint gutter and actions follow the same capability");
                    if (supported)
                    {
                        UpdateProjectActions(busy: true);
                        Check(!DebugStartButton.IsEnabled && !DebugStartMenu.IsEnabled, label + ": supported entry still obeys busy state");
                        var busyKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F5)
                        {
                            RoutedEvent = Keyboard.PreviewKeyDownEvent
                        };
                        Check(HandleDebugKey(busyKey, ModifierKeys.Control) && busyKey.Handled && !services.Debugger.IsActive,
                            label + ": busy shortcut is consumed without starting debug or another command");
                        UpdateProjectActions(busy: false);
                        continue;
                    }
                    Check(DebugStartButton.ToolTip is string message && message == Mon51UnavailableMessage &&
                        Equals(DebugStartMenu.ToolTip, message) && ToolTipService.GetShowOnDisabled(DebugStartButton) &&
                        ToolTipService.GetShowOnDisabled(DebugStartMenu), label + ": disabled controls explain the unsupported target");
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.F5)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    Check(HandleDebugKey(key, ModifierKeys.Control) && key.Handled && Status.Text == Mon51UnavailableMessage &&
                        !services.Debugger.IsActive && !projectActionsBusy,
                        label + ": unsupported shortcut is consumed and explains why debugging is unavailable");
                    var log = BuildLog.Text;
                    DebugStart_Click(DebugStartButton, new RoutedEventArgs());
                    await pendingOperation;
                    Check(Status.Text == Mon51UnavailableMessage && !services.Debugger.IsActive &&
                        source.IsDirty && SelectedBuildSettings == draft && BuildLog.Text == log && !projectActionsBusy,
                        label + ": direct debug command is rejected before saving, preparing or connecting");
                    var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
                    var afterContents = await Task.WhenAll(after.Select(path => File.ReadAllBytesAsync(path)));
                    Check(files.SequenceEqual(after) && contents.Zip(afterContents).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
                        label + ": direct command leaves all project files untouched");
                    if (device == "STC8H8K64U")
                    {
                        Render(this, Path.Combine(directory, "mon51-unsupported-" + theme.Id + ".png"));
                    }
                }
            }
        }
        finally
        {
            source.Buffer.Text = source.Source.Text;
            currentProjectManifest = original;
            supportsDownload = originalDownload;
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
            UpdateProjectActions(busy: false);
        }
    }
}

namespace StudioX.Desktop;

using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在额外夹具中核对下载页只读、跳转和跨工程隐藏；不访问串口或执行下载。</summary>
    private async Task RenderManagedClockScopePreviewAsync(string directory, string source, string pluginId,
        Dictionary<string, string> values, Action<bool, string> check)
    {
        async Task Layout()
        {
            await Dispatcher.InvokeAsync(() => { FlushPluginPanels(); UpdateLayout(); }, DispatcherPriority.ContextIdle);
            await Task.Delay(100);
        }
        async Task Wait(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Layout();
                if (condition())
                {
                    return;
                }
            }
            throw new InvalidOperationException("托管时钟界面未完成：" + StcIspStatus.Text + "\n" + PluginManager.DiagnosticLog.Text);
        }
        string Clone(string name, bool associated)
        {
            var target = Path.Combine(directory, name);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
                {
                    continue;
                }
                var copy = PathBoundary.Resolve(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            var manifestPath = Path.Combine(target, ".studiox/project.json");
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath), JsonStore.Options)!;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with
            {
                Name = name,
                GraphicalConfiguration = associated ? manifest.GraphicalConfiguration : null
            }, JsonStore.Options), new UTF8Encoding(false));
            return target;
        }

        var saved = Clone("managed_clock_saved", true);
        // 这是额外离线夹具；原始交互工程的磁盘文件保持未修改，保留未保存检查的意义。
        await using (var broker = new PluginWorkspaceBroker(services, saved, new McuConfigurationPreviewAuthorizer(saved), () => Task.FromResult(false)))
        {
            await using (var workspace = await services.PluginManager.OpenWorkspaceAsync(saved, broker.CallAsync))
            {
                // 生成后的夹具已经持有完整 GPIO 参数；重新分配会清空它并令凭据过期。
                var state = await services.ProjectClocks.ReadAsync(saved);
                check(state is { Managed: true, Ready: true }, "generated clone retains valid clock and output receipts");
            }
        }
        await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.No);
        check(!pluginActivities.ContainsKey(pluginId), "closing project removes configurator activity entry");
        await OpenProjectAsync(saved, CancellationToken.None);
        await ShowProjectDetailsAsync();
        await Wait(() => loadedProjectClock is { Managed: true, Ready: true } && loadedStcIspSettings is not null &&
            pluginActivities.ContainsKey(pluginId) && pluginPanels.Values.SelectMany(panel => panel.Renderer.ConfigurationViews).Any());
        check(!StcClockModePicker.IsEnabled && StcClockFrequencyBox.IsReadOnly, "owned download clock source and frequency are read-only");
        check(StcPortPicker.IsEnabled && StcBaudPicker.IsEnabled, "transport port and baud remain editable with owned clock");
        check(loadedStcIspSettings!.ClockFrequencyHz == 24000000 && loadedProjectClock!.SystemFrequencyHz == 12000000 &&
            StcClockFrequencyBox.Text == "24", "download shows source 24 MHz while compiler clock is divided 12 MHz");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Layout();
            StcBaudPicker.BringIntoView();
            await Layout();
            Render(this, Path.Combine(directory, "managed-download-" + theme.Id + ".png"));
        }
        OpenManagedClock_Click(this, new RoutedEventArgs());
        await Layout();
        check(pluginPanels.Values.SelectMany(panel => panel.Renderer.ConfigurationViews).Single().Page == 1,
            "download link navigates to existing plugin clock tree");
        var ordinary = Clone("ordinary_project_scope", false);
        await OpenProjectAsync(ordinary, CancellationToken.None);
        await ShowProjectDetailsAsync();
        await Wait(() => !pluginActivities.ContainsKey(pluginId) && loadedProjectClock is { Managed: false });
        check(!ActivePluginContributions.Any(active => active.Id == pluginId) && !pluginPanels.Keys.Any(key => key.StartsWith(pluginId + "/", StringComparison.Ordinal)),
            "switching to same MCU ordinary project removes commands, panel and activity");
        check(StcClockModePicker.IsEnabled && !StcClockFrequencyBox.IsReadOnly, "ordinary project retains manual download clock controls");
        await CloseProjectAsync(CancellationToken.None);
        check(!pluginActivities.ContainsKey(pluginId), "no-project context keeps configurator hidden");
    }
}

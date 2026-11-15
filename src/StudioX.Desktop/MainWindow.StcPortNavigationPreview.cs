namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>实际下载/调试入口的离线导航回归；缺少固件和工具，禁止连接硬件。</summary>
    public async Task RenderStcPortNavigationPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                File.WriteAllText(Path.Combine(directory, "failed-state.txt"),
                    $"{name}\nDownloadTab={StcDownloadTab.IsSelected}\nPackagesTab={PackagesTab.IsSelected}\nStatus={Status.Text}\nBuild={BuildSettingsStatus.Text}\nIsp={StcIspStatus.Text}\nLog={BuildLog.Text}");
                Render(this, Path.Combine(directory, "failed.png"));
                throw new InvalidOperationException(name + " · " + Status.Text);
            }
            checks.Add(name);
        }
        var fontSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "port-custom",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#E8C070",
                ["OnAccent"] = "#201C13"
            }
        };
        try
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                Width = 1300;
                Height = 1000;
                foreach (var device in new[] { "STC89C52RC", "STC12C5A60S2", "IAP15F2K61S2", "IAP15L2K61S2", "STC8G1K08", "STC8H8K64U", "AI8051U-34K64", "STC32G12K128" })
                {
                    var mx = device is "AI8051U-34K64" or "STC32G12K128";
                    var root = Path.Combine(directory, "projects", device + "-" + theme.Id);
                    projectDirectory = root;
                    currentProjectManifest = new(1, device, mx ? "stc.ai8051u-mx251" : "stc.stc8", mx ? "0.2.171" : "0.1.9", "", device, "bare",
                        mx ? "studiox.mx251" : "stc.sdcc", mx ? "0.0.170" : "1.0.0", mx ? "mx251-v0.0170" : "sdcc-4.5.0-15242");
                    ConfigureBuildSettingsForProject();
                    ConfigureStcIspForProject();
                    ConfigureMx251IspForProject();
                    if (!ProjectCapabilities.For(currentProjectManifest).Download)
                    {
                        Check(Mx251IspPanel.Visibility == Visibility.Collapsed,
                            device + " / " + theme.Id + ": unsupported target keeps its download capability boundary");
                        continue;
                    }
                    if (mx)
                    {
                        Directory.CreateDirectory(root);
                        ApplyMx251Settings(new(new("mx251.json", "{}", Encoding.UTF8, "preview", false), "large", "large"));
                        ApplyMx251Isp(new(DownloadMode: Mx251DownloadMode.Serial), []);
                    }
                    else
                    {
                        await WriteStcBuildSettingsPreviewProjectAsync(root, currentProjectManifest);
                        stcRamLimits = await services.Builds.ReadStcRamLimitsAsync(root);
                        ApplyBuildSettings(new());
                        var deviceInfo = (await JsonStore.ReadAsync<StudioX.Packages.PackManifest>(Path.Combine(root, "device/manifest.json"))).Devices[0];
                        ApplyStcIspSettings(new(), StcIspCapabilities.For(deviceInfo), new(true, null, null, "离线端口回归"), []);
                    }
                    Directory.CreateDirectory(Path.Combine(root, "src"));
                    var file = Path.Combine(root, "src/main.c");
                    const string source = "void main(void) { while (1) {} }\n";
                    await File.WriteAllTextAsync(file, source);
                    ShowSource(new("src/main.c", source, Encoding.UTF8, "preview", false));
                    var editor = activeEditor!;
                    editor.Buffer.Insert(0, "// unsaved draft\n");
                    var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
                    Download_Click(DownloadButton, new RoutedEventArgs());
                    await pendingOperation;
                    await Dispatcher.InvokeAsync(() =>
                    {
                    }, DispatcherPriority.ContextIdle);
                    UpdateLayout();
                    var picker = mx ? Mx251SerialPortPicker : StcPortPicker;
                    Check(StcDownloadTab.IsSelected && !PackagesTab.IsSelected && (mx ? Mx251IspStatus.Text : StcIspStatus.Text).Contains("COM"), device + " / " + theme.Id + ": missing COM opens download settings");
                    var bounds = picker.TransformToAncestor(WorkspaceTabs).TransformBounds(new Rect(picker.RenderSize));
                    Check(picker.IsVisible && bounds.Top >= 0 && bounds.Bottom <= WorkspaceTabs.ActualHeight,
                        device + " / " + theme.Id + ": COM selector scrolled into view");
                    Check(editor.IsDirty && await File.ReadAllTextAsync(file) == source && files.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()),
                        device + " / " + theme.Id + ": no save, build or settings rewrite before port selection");
                    if (device is "IAP15F2K61S2" or "IAP15L2K61S2")
                    {
                        ShowDocument(editor.Tab);
                        await StartMon51DebugAsync(CancellationToken.None);
                        Check(StcDownloadTab.IsSelected && StcIspStatus.Text.Contains("串口仿真"), theme.Id + ": Mon51 missing port uses the same settings page");
                    }
                    if (device is "IAP15L2K61S2" or "AI8051U-34K64")
                    {
                        ShowDocument(editor.Tab);
                        picker.Text = "COM99";
                        var reads = 0;
                        var allowed = await RequireStcSerialPortAsync(root, "COM15", () =>
                        {
                            reads++;
                            return Task.FromResult(new[] { "COM17" });
                        }, CancellationToken.None);
                        Check(!allowed && reads == 1 && StcDownloadTab.IsSelected && picker.Text == "COM99" && (mx ? Mx251IspStatus.Text : StcIspStatus.Text).Contains("COM15"),
                            device + " / " + theme.Id + ": vanished saved port locates settings and preserves connection draft");
                        ShowDocument(editor.Tab);
                        Check(await RequireStcSerialPortAsync(root, "com17", () => Task.FromResult(new[] { "COM17" }), CancellationToken.None) && !StcDownloadTab.IsSelected,
                            device + " / " + theme.Id + ": existing serial port continues without navigation");
                        await ShowStcDownloadIssueAsync(root, "保存的串口 COM15 当前未连接；请重新选择并保存下载串口。", true, CancellationToken.None);
                        Render(this, Path.Combine(directory, device + "-" + theme.Id + ".png"));
                    }
                    editor.Buffer.Text = editor.Source.Text;
                }
            }
            await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
            {
                passed = true,
                checks,
                hardware = false
            });
        }
        finally
        {
            foreach (var editor in editorDocuments)
            {
                editor.Buffer.Text = editor.Source.Text;
            }
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = fontSize;
        }
    }
}

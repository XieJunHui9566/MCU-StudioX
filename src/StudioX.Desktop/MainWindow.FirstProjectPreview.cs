namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Onboarding;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离数据和新建 STM32 工程内验证引导，不连接设备。</summary>
    public async Task RenderFirstProjectPreviewAsync(string directory, string archive)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        Check(!await services.FirstProjectGuide.IsDismissedAsync(), "fresh user preference defaults to onboarding");
        await OfferFirstProjectGuideAsync();
        Check(firstProjectTab is not null && WorkspaceTabs.SelectedItem == firstProjectTab, "first launch with no recent project offers real guide");
        var originalTab = firstProjectTab!;
        Check(FirstProjectGuideService.Steps(GuideProgress()).Count == 7 && firstProjectView!.ActionButton.IsEnabled, "seven steps available without a project");
        firstProjectView!.Select(4);
        Check(!firstProjectView.ActionButton.IsEnabled, "compile action unavailable until project open");
        await CloseWorkspaceTabAsync(originalTab);
        await ShowFirstProjectAsync();
        Check(firstProjectTab == originalTab && originalTab.Visibility == Visibility.Visible, "closed guide reopens in same tab and retains step");
        await RunGuideActionAsync("create");
        Check(projectDirectory is null && WorkspaceTabs.SelectedItem == PackagesTab && DevicePicker.SelectedIndex == -1 && TemplatePicker.SelectedIndex == -1, "guide opens genuine creator without guessing target or template");
        var pack = await ImportPackForSelectionAsync(archive, CancellationToken.None);
        var device = pack.Manifest.Devices.First(device => device.Id == "STM32F103C8");
        SelectPack(pack);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<StudioX.Packages.DeviceDefinition>().Single(candidate => candidate.Id == device.Id);
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<StudioX.Packages.ProjectTemplate>().Single(template => template.Id == "hal");
        Check(CreateProjectButton.IsEnabled, "exact device and HAL template enable actual creator");
        var fixture = Path.Combine(directory, "first_firmware");
        await services.Projects.CreateAsync(pack, device.Id, "hal", "first_firmware", fixture);
        await OpenProjectAsync(fixture, CancellationToken.None);
        await ShowFirstProjectAsync();
        Check(FirstProjectGuideService.Steps(GuideProgress())[1].Complete && !guideSaved && guideBuild is null, "opened generated project reports current target and fresh evidence");
        await RunGuideActionAsync("health");
        Check(guideHealthPassed, "real fast health check updates current guide evidence");
        await RunGuideActionAsync("source");
        Check(activeEditor?.Source.RelativePath == "src/main.c", "source action opens actual template entry");
        activeEditor!.Buffer.Insert(0, "// 我的第一个工程\n");
        await RunGuideActionAsync("save");
        Check(guideSaved && !activeEditor.IsDirty && File.ReadAllText(Path.Combine(fixture, "src/main.c")).Contains("我的第一个工程"), "save action writes modified source to isolated project");
        await RunGuideActionAsync("build");
        await pendingOperation;
        Check(guideBuild is { Success: true, ExitCode: 0 } && guideBuild.Artifacts.Count > 0, "real F7 path builds STM32 HAL and records current artifact paths");
        await File.WriteAllTextAsync(Path.Combine(directory, "build-success.log"), BuildLog.Text);
        await ShowFirstProjectAsync();
        firstProjectView.Select(5);
        Check(firstProjectView.EvidenceText.Text.Contains(".elf", StringComparison.OrdinalIgnoreCase), "result step shows genuine generated firmware");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            firstProjectView.Select(3);
            await Layout();
            Check(firstProjectView.BodyScroll.ActualWidth > 400 && firstProjectView.StepList.Items.Count == 7, theme.Id + " guide retains readable navigation and scrollable content");
            Render(this, Path.Combine(directory, "guide-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        Width = MinWidth;
        Height = MinHeight;
        firstProjectView.Select(1);
        await Layout();
        Check(firstProjectView.BodyScroll.ActualWidth > 230, "minimum window keeps guide body accessible");
        Render(this, Path.Combine(directory, "guide-compact.png"));
        activeEditor!.Buffer.Insert(0, "#error GUIDE_EXPECTED_FAILURE\n");
        Check(guideBuild is null, "editing invalidates successful build evidence immediately");
        await RunGuideActionAsync("build");
        await pendingOperation;
        Check(guideBuild is { Success: false } && BuildLog.Text.Contains("GUIDE_EXPECTED_FAILURE"), "failed real build preserves original error and removes success marking");
        await File.WriteAllTextAsync(Path.Combine(directory, "build-expected-failure.log"), BuildLog.Text);
        activeEditor.Buffer.Remove(0, "#error GUIDE_EXPECTED_FAILURE\n".Length);
        await RunGuideActionAsync("save");
        await CloseProjectAsync(CancellationToken.None);
        Check(!guideSaved && !guideHealthPassed && guideBuild is null, "closing project clears project-scoped completion evidence");
        var script = FirstProjectGuideService.Steps(new(ProjectDirectory: fixture, Script: true));
        Check(script[4].Action == "help" && script[4].Title == "检查脚本" && !script[4].Complete, "MicroPython uses script help and never enables firmware compile");
        var experimental = FirstProjectGuideService.Steps(new(ProjectDirectory: fixture, Experimental: true));
        Check(experimental[4].Action == "help" && !experimental[4].Complete, "experimental framework does not claim native validation");
        Check(FirstProjectGuideService.Steps(new(Espressif: true))[4].HelpTopic == "esp-idf-errors", "IDF has relevant native diagnostics help");
        await ShowFirstProjectAsync();
        await firstProjectView.FinishRequested!();
        Check(await new FirstProjectGuideService(services.DataDirectory).IsDismissedAsync(), "finish persists independent user preference across service reload");
        await OfferFirstProjectGuideAsync();
        Check(WorkspaceTabs.SelectedItem == WelcomeTab, "dismissed guide does not auto-open again");
        await ShowFirstProjectAsync();
        Check(firstProjectTab == originalTab, "dismissed guide remains manually accessible");
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            status = "passed",
            checks,
            hardwareAccess = false
        });
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}

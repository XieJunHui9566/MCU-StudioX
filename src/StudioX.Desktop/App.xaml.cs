namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudioX.Application;

public partial class App : System.Windows.Application
{
    // 安装器据此要求先关闭 IDE；不强杀进程，保留用户保存未完成修改的机会。
    private Mutex? installationMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowMouseWheel.Install();
        installationMutex = new Mutex(false, "MCUStudioX.Desktop.InstallLock");
        var showProductivity = e.Args is ["--show-ide-next", _, _];
        var acceptanceWorkbench = e.Args is ["--acceptance-workbench", _, _];
        var showAgentWorkspace = e.Args is ["--show-agent-workspace", _, _];
        var agentWorkspacePreview = e.Args is ["--preview-agent-workspace", _, _];
        var productivityPreview = e.Args is ["--preview-ide-next", _, _] or ["--preview-ide-next-recovery", _, _];
        var smoke = e.Args is ["--smoke", _];
        var helpPreview = e.Args is ["--preview-help", _];
        var firstProjectPreview = e.Args is ["--preview-first-project", _, _];
        var projectToolsPreview = e.Args is ["--preview-project-tools", _, _, _];
        var idfVersionsPreview = e.Args is ["--preview-idf-versions", _, _, _, _];
        var developmentComponentsPreview = e.Args is ["--preview-development-components", _, _, _];
        var faultPeripheralsPreview = e.Args is ["--preview-fault-peripherals", _, _];
        var codeTemplatesPreview = e.Args is ["--preview-code-templates", _];
        var peripheralDevelopmentPreview = e.Args is ["--preview-peripheral-development", _, _, _];
        var projectHealthPreview = e.Args is ["--preview-project-health", _, _];
        var toolManagementPreview = e.Args is ["--preview-tool-management", _, _] or ["--preview-tool-management", _, _, _];
        var diagnosticsPreview = e.Args is ["--preview-diagnostics", _];
        var openOcdPlotPreview = e.Args is ["--preview-openocd-plot", _];
        var pluginLabsPreview = e.Args is ["--preview-plugin-labs", _, _];
        var workspaceEditingPreview = e.Args is ["--preview-workspace-editor", _, _] or ["--preview-editor-recovery", _, _]
            or ["--preview-workspace-editor", _, _, _] or ["--preview-editor-recovery", _, _, _];
        var showDebugDemo = e.Args is ["--show-debug-demo", _] or ["--show-debug-demo", _, _];
        var showBreakpointsDemo = e.Args is ["--show-breakpoints-demo", _] or ["--show-breakpoints-demo", _, _];
        var preview = e.Args is ["--preview-ui", _];
        var windowLayoutPreview = e.Args is ["--preview-window-layout", _];
        var editorPreview = e.Args is ["--preview-editor", _, _];
        var completionPreview = e.Args is ["--preview-completion", _, _];
        var cmakePreview = e.Args is ["--preview-cmake", _, _];
        var pythonPreview = e.Args is ["--preview-python", _];
        var microPythonPreview = e.Args is ["--preview-micropython", _, _, _];
        var documentsPreview = e.Args is ["--preview-documents", _, _];
        var vePreview = e.Args is ["--preview-ve", _, _, _];
        var ag32MappingPreview = e.Args is ["--preview-ag32-mapping", _, _];
        var hdlPreview = e.Args is ["--preview-hdl", _, _];
        var hdlWorkflowPreview = e.Args is ["--preview-hdl-workflow", _, _];
        var navigationPreview = e.Args is ["--preview-navigation", _, _];
        var explorerPreview = e.Args is ["--preview-explorer", _, _];
        var buildPreview = e.Args is ["--preview-build", _, _];
        var buildMemoryPreview = e.Args is ["--preview-memory", _, _];
        var editingPreview = e.Args is ["--preview-editing", _, _];
        var bracketsPreview = e.Args is ["--preview-brackets", _, _];
        var projectPreview = e.Args is ["--preview-project", _, _];
        var cubeMxPreview = e.Args is ["--preview-cubemx", _, _];
        var downloadPreview = e.Args is ["--preview-download", _, _];
        var espressifPreview = e.Args is ["--preview-espressif", _, _];
        var espressifModulePreview = e.Args is ["--preview-espressif-module", _, _];
        var pluginsPreview = e.Args is ["--preview-plugins", _] or ["--preview-plugins", _, _];
        var debugPluginsPreview = e.Args is ["--preview-debug-plugins", _, _, _];
        var productWorkflowsPreview = e.Args is ["--preview-product-workflows", _, _, _];
        var debugPreview = e.Args is ["--preview-debug", _, _];
        var rtosPreview = e.Args is ["--preview-rtos", _, _];
        var packCatalogPreview = e.Args is ["--preview-pack-catalog", _, _, _];
        var breakpointsPreview = e.Args is ["--preview-breakpoints", _, _];
        var importPerformancePreview = e.Args is ["--preview-import-performance", _, _];
        var largeProjectPreview = e.Args is ["--preview-large-project", _, _];
        var projectOpenPerformancePreview = e.Args is ["--preview-project-open-performance", _, _];
        var stm32Preview = e.Args is ["--preview-stm32", _, _] or ["--preview-stm32", _, _, _];
        var rp2350Preview = e.Args is ["--preview-rp2350", _, _];
        var rp2040Preview = e.Args is ["--preview-rp2040", _, _];
        var lvglPreview = e.Args is ["--preview-lvgl-ui", _, _];
        var lvglSetupPreview = e.Args is ["--preview-lvgl-setup", _, _, _, _];
        var zephyrDevicetreePreview = e.Args is ["--preview-zephyr-devicetree", _, _];
        var anyPreview = productivityPreview || workspaceEditingPreview || microPythonPreview || pythonPreview || hdlWorkflowPreview || hdlPreview || ag32MappingPreview || pluginsPreview || preview || windowLayoutPreview || editorPreview || completionPreview || cmakePreview || documentsPreview || vePreview || navigationPreview || explorerPreview || buildPreview || buildMemoryPreview || editingPreview || bracketsPreview || stm32Preview || rp2350Preview || rp2040Preview || projectPreview || cubeMxPreview || importPerformancePreview || downloadPreview || espressifPreview || espressifModulePreview || debugPreview || rtosPreview || packCatalogPreview || breakpointsPreview || lvglPreview || lvglSetupPreview || zephyrDevicetreePreview;
        anyPreview |= agentWorkspacePreview || diagnosticsPreview || openOcdPlotPreview || pluginLabsPreview || helpPreview || firstProjectPreview || projectHealthPreview || toolManagementPreview || largeProjectPreview || projectOpenPerformancePreview;
        anyPreview |= debugPluginsPreview || productWorkflowsPreview || projectToolsPreview;
        anyPreview |= faultPeripheralsPreview;
        anyPreview |= codeTemplatesPreview;
        anyPreview |= peripheralDevelopmentPreview;
        anyPreview |= developmentComponentsPreview;
        anyPreview |= idfVersionsPreview;
        var data = (acceptanceWorkbench || showAgentWorkspace || showProductivity || showDebugDemo || showBreakpointsDemo) && e.Args.Length == 3 ? Path.GetFullPath(e.Args[2]) : smoke || anyPreview ? Path.Combine(Path.GetFullPath(e.Args[1]), "user-data") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX");
        var services = new WorkbenchService(acceptanceWorkbench ? Path.GetFullPath(e.Args[1])
            : peripheralDevelopmentPreview ? Path.GetFullPath(e.Args[3])
            : workspaceEditingPreview && e.Args.Length == 4 ? Path.GetFullPath(e.Args[3])
            : toolManagementPreview && e.Args.Length == 4 ? Path.GetFullPath(e.Args[3])
            : idfVersionsPreview ? Path.GetFullPath(e.Args[3]) : Path.Combine(AppContext.BaseDirectory, "runtime"), data);
        var window = new MainWindow(services);
        MainWindow = window;
        if (smoke || anyPreview)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
        }
        window.Show();
        await window.InitializeAsync(loadGitHubAccounts: !smoke && !anyPreview && !showProductivity && !showAgentWorkspace && !acceptanceWorkbench);
        if (anyPreview)
        {
            var directory = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(directory);
            try
            {
                if (peripheralDevelopmentPreview)
                {
                    await window.RenderPeripheralDevelopmentPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (developmentComponentsPreview)
                {
                    await window.RenderDevelopmentComponentsPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (codeTemplatesPreview)
                {
                    await window.RenderCodeTemplatesPreviewAsync(directory);
                }
                else if (faultPeripheralsPreview)
                {
                    await window.RenderFaultPeripheralsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (projectToolsPreview)
                {
                    await window.RenderProjectToolsPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (idfVersionsPreview)
                {
                    await window.RenderIdfVersionsPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[4]));
                }
                else if (productWorkflowsPreview)
                {
                    await window.RenderProductWorkflowsPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (debugPluginsPreview)
                {
                    await window.RenderDebugPluginsPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (firstProjectPreview)
                {
                    await window.RenderFirstProjectPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (largeProjectPreview)
                {
                    await window.MeasureLargeProjectAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (projectOpenPerformancePreview)
                {
                    await window.MeasureProjectOpenPerformanceAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (toolManagementPreview)
                {
                    await window.RenderToolManagementPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (projectHealthPreview)
                {
                    await window.RenderProjectHealthPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (helpPreview)
                {
                    await window.RenderHelpPreviewAsync(directory);
                }
                else if (pluginLabsPreview)
                {
                    await window.RenderPluginLabsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (openOcdPlotPreview)
                {
                    await window.RenderOpenOcdPlotPreviewAsync(directory);
                }
                else if (diagnosticsPreview)
                {
                    await window.RenderDiagnosticsPreviewAsync(directory);
                }
                else if (agentWorkspacePreview)
                {
                    await window.RenderAgentWorkspacePreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (productivityPreview)
                {
                    await window.RenderProductivityPreviewAsync(directory, Path.GetFullPath(e.Args[2]), e.Args[0] == "--preview-ide-next-recovery");
                }
                else if (workspaceEditingPreview)
                {
                    await window.RenderWorkspaceEditingPreviewAsync(directory, Path.GetFullPath(e.Args[2]), e.Args[0] == "--preview-editor-recovery");
                }
                else if (hdlWorkflowPreview)
                {
                    await window.RenderHdlWorkflowPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (hdlPreview)
                {
                    await window.RenderHdlPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (ag32MappingPreview)
                {
                    await window.RenderAg32MappingPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (pluginsPreview)
                {
                    await window.RenderPluginsPreviewAsync(directory, e.Args.Length == 3 ? Path.GetFullPath(e.Args[2]) : null);
                }
                else if (espressifModulePreview)
                {
                    await window.RenderEspressifModulePreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (espressifPreview)
                {
                    await window.RenderEspressifPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (vePreview)
                {
                    await window.RenderVePreviewAsync(directory, Path.GetFullPath(e.Args[2]), e.Args[3]);
                }
                else if (windowLayoutPreview)
                {
                    await window.RenderWindowLayoutPreviewAsync(directory);
                }
                else if (lvglSetupPreview)
                {
                    await window.RenderLvglSetupPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]), Path.GetFullPath(e.Args[4]));
                }
                else if (lvglPreview)
                {
                    await window.RenderLvglPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (buildMemoryPreview)
                {
                    await window.RenderBuildMemoryPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (editingPreview)
                {
                    await window.RenderEditingPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (bracketsPreview)
                {
                    await window.RenderBracketsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (debugPreview)
                {
                    await window.RenderDebugPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (rtosPreview)
                {
                    await window.RenderFreeRtosPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (packCatalogPreview)
                {
                    await window.RenderPackCatalogPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (breakpointsPreview)
                {
                    await window.RenderBreakpointsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (downloadPreview)
                {
                    await window.RenderDownloadPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (cubeMxPreview)
                {
                    await window.RenderCubeMxPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (importPerformancePreview)
                {
                    await window.MeasureImportPerformanceAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (projectPreview)
                {
                    await window.RenderProjectPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (stm32Preview)
                {
                    await window.RenderStm32PreviewAsync(directory, Path.GetFullPath(e.Args[2]), e.Args.Length == 4 ? Path.GetFullPath(e.Args[3]) : null);
                }
                else if (rp2350Preview)
                {
                    await window.RenderRp2350PreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (rp2040Preview)
                {
                    await window.RenderRp2040PreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (buildPreview)
                {
                    await window.RenderBuildPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (explorerPreview)
                {
                    await window.RenderExplorerPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (navigationPreview)
                {
                    await window.RenderNavigationPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (documentsPreview)
                {
                    await window.RenderDocumentsPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (microPythonPreview)
                {
                    await window.RenderMicroPythonPreviewAsync(directory, Path.GetFullPath(e.Args[2]), Path.GetFullPath(e.Args[3]));
                }
                else if (pythonPreview)
                {
                    await window.RenderPythonPreviewAsync(directory);
                }
                else if (cmakePreview)
                {
                    await window.RenderCMakePreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (completionPreview)
                {
                    await window.RenderCompletionPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (editorPreview)
                {
                    await window.RenderEditorPreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else if (zephyrDevicetreePreview)
                {
                    await window.RenderZephyrDevicetreePreviewAsync(directory, Path.GetFullPath(e.Args[2]));
                }
                else
                {
                    await window.RenderPreviewAsync(directory);
                }
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }
            return;
        }
        if (!smoke)
        {
            if (showAgentWorkspace)
            {
                await window.ShowAgentWorkspacePreviewAsync(Path.GetFullPath(e.Args[1]));
                return;
            }
            if (showProductivity)
            {
                await window.ShowProductivityWorkspaceAsync(Path.GetFullPath(e.Args[1]));
                return;
            }
            if (showDebugDemo || showBreakpointsDemo)
            {
                if (showBreakpointsDemo)
                {
                    await window.ShowSpecialBreakpointDemoAsync(e.Args[1]);
                }
                else
                {
                    await window.ShowDebugDemoAsync(e.Args[1]);
                }
                if (services.Debugger.State == StudioX.Engine.Debugging.DebugState.Stopped)
                {
                    await StudioX.Foundation.JsonStore.WriteAsync(Path.Combine(data, "debug-demo-ready.json"), new
                    {
                        processId = Environment.ProcessId,
                        project = services.Debugger.ProjectDirectory,
                        state = "Stopped",
                        mode = "offline"
                    });
                }
                return;
            }
            if (e.Args.Length == 0 || e.Args is ["--first-project"])
            {
                await window.RestoreLastEditorSessionAsync();
                if (e.Args.Length == 0)
                {
                    await window.OfferFirstProjectGuideAsync();
                }
                else
                {
                    await window.OpenFirstProjectGuideAsync();
                }
            }
            else if (e.Args is ["--new-project"])
            {
                await window.ShowNewProjectAsync();
            }
            else if (e.Args is ["--import-cubemx"])
            {
                await window.ShowCubeMxImportAsync();
            }
            else if (e.Args is ["--import-cubemx", var cubeDirectory])
            {
                await window.ShowCubeMxImportAsync(cubeDirectory);
            }
            else if (e.Args is ["--new-project", var packId, var deviceId])
            {
                await window.ShowNewProjectAsync(packId, deviceId);
            }
            else if (e.Args is ["--open", var workflowProject, "--hdl-workflow"])
            {
                await window.OpenFromCommandLineAsync(workflowProject);
                await window.ShowHdlWorkflowPageAsync();
            }
            else if (e.Args is ["--open", var hdlProject, "--hdl-schematic"])
            {
                await window.OpenFromCommandLineAsync(hdlProject);
                await window.ShowHdlSchematicPageAsync();
            }
            else if (e.Args is ["--open", var mappingProject, "--ag32-pin-mapping"])
            {
                await window.OpenFromCommandLineAsync(mappingProject);
                await window.ShowAg32PinMappingPageAsync();
            }
            else if (e.Args is ["--open", var lvglProject, "--lvgl-preview"])
            {
                await window.OpenFromCommandLineAsync(lvglProject);
                await window.ShowLvglPreviewAsync(start: true);
            }
            else if (e.Args is ["--open", var project])
            {
                await window.OpenFromCommandLineAsync(project);
            }
            else if (e.Args is ["--open", var fileProject, "--file", var relativePath])
            {
                await window.OpenFromCommandLineAsync(fileProject, relativePath);
            }
            else if (e.Args.Length > 3 && e.Args[0] == "--open" && e.Args[2] == "--files")
            {
                await window.OpenFromCommandLineAsync(e.Args[1], e.Args[3..]);
            }
            return;
        }
        try
        {
            var output = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(output);
            await window.ExerciseSimulationAsync();
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                window.ApplyTheme(theme);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, theme.Id + ".png"));
                encoder.Save(file);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "Window initialized; two subscribers received simulation frames; dark/light rendered.\n");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(e.Args[1]), "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { window.Close(); }
    }
}

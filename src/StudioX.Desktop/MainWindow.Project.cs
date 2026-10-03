namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task OpenProjectAsync(string directory, CancellationToken token)
    {
        directory = Path.GetFullPath(directory);
        if (string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            if (activeEditor is not null)
            {
                ShowDocument(activeEditor.Tab);
            }
            return;
        }
        // 先确认目标仍是工程，再关闭当前文档和调试会话。
        var project = await ProjectService.ReadAsync(directory, token);
        if (!await ConfirmDocumentsAsync())
        {
            return;
        }
        await projectTransitions.StopCurrentAsync(token);
        CancelBuildMemoryRefresh();
        // 官方 SDK 示例保留原文件名和目录；由创建记录指定入口，旧工程继续使用原有默认位置。
        var mainPath = project.EntryFile ?? (project.Kind == ProjectKind.CubeMx ? "Core/Src/main.c" : "src/main.c");
        var source = !restoringEditorSession && services.Files.FileExists(directory, mainPath)
            ? await services.Files.ReadAsync(directory, mainPath, token)
            : null;
        ClearEditorDocuments();
        projectDirectory = directory;
        ResetFirstProjectEvidence();
        lastFailure = "";
        healthDirectory = directory;
        projectHealthView?.Invalidate();
        LvglPreview.SetProject(directory);
        ClearHdlSchematic();
        ResetAiForProjectChange();
        RefreshSkillsForProjectChange();
        GitGraph.SetProject(directory);
        GitHubWorkspace.SetProject(directory);
        BuildMemory.SetMessage("正在读取上次构建的占用…");
        SetProjectDetailsMode(project);
        await ProjectTerminal.SetProjectAsync(directory);
        await services.Debugger.OpenProjectAsync(directory, token);
        ApplyDownloadConfiguration(null);
        // 新建入口负责关闭确认；器件与模板入口仅展示当前工程配置。
        PackagesTab.Visibility = Visibility.Collapsed;
        ProjectLabel.Text = directory;
        WindowProjectTitle.Text = project.Name;
        Title = project.Name + " — MCU StudioX";
        BuildConfiguration.Text = project.Kind == ProjectKind.Zephyr
            ? project.Name + " · Zephyr 实验模式"
            : project.Name + " · " + (project.CubeMx?.ConfigurePreset ?? project.CubeMx?.BuildType ?? "Debug");
        DeviceLabel.Text = project.Kind == ProjectKind.Zephyr
            ? "板级目标 / " + project.Zephyr!.BoardTarget
            : "器件 / " + project.DeviceId;
        ToolsetLabel.Text = project.Kind == ProjectKind.Zephyr
            ? "Zephyr " + project.Zephyr!.ZephyrVersion + " · 实验模式"
            : $"开发环境组件 / {project.ToolsetId} {project.ToolsetVersion}";
        ApplyDownloadConfiguration(await services.Downloads.ConfigurationAsync(directory, token));
        if (source is not null)
        {
            ShowSource(source);
        }
        else
        {
            ShowDocument(WelcomeTab);
        }
        // 首次挂入编辑器会触发布局与语法渲染，先处理输入和这一帧，再展开工程树，避免累计成一次长停顿。
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        await PopulateProjectTreeAsync(project.Name, token: token);
        if (project.Kind == ProjectKind.MicroPython)
        {
            BuildConfiguration.Text = project.Name + " · MicroPython";
            ToolsetLabel.Text = "MicroPython " + project.MicroPython!.Version + " · " + project.MicroPython.Board;
            BuildMemory.SetMessage("脚本在板上解释器中运行；请使用 MicroPython 页面。");
            MicroPythonPanel.SetProject(directory, project.MicroPython);
            await services.RecentProjects.RememberAsync(project.Name, directory, token);
            await RefreshRecentAsync(token);
            Status.Text = "已打开 " + project.Name + " · MicroPython 编辑已就绪；点击顶部下载按钮选择串口并下载脚本。";
            return;
        }
        MicroPythonPanel.SetProject(null, null);
        if (project.Kind != ProjectKind.Zephyr)
        {
            QueueBuildMemoryRefresh(directory);
        }
        await services.RecentProjects.RememberAsync(project.Name, directory, token);
        await RefreshRecentAsync(token);
        await ReloadPluginWorkspaceAsync(token);
        if (project.Kind == ProjectKind.Zephyr)
        {
            BuildMemory.SetMessage("Zephyr 实验模式：板级构建尚待验证。");
            var boardDts = await services.ZephyrProjects.FindBoardDevicetreeAsync(directory, project, token);
            ZephyrDeviceTreeButton.IsEnabled = boardDts is not null;
            if (boardDts is not null)
            {
                await ShowZephyrBoardDevicetreeAsync(boardDts, token);
            }
            else
            {
                Status.Text = "已打开 " + project.Name + " · Zephyr 实验模式；此工程没有板级 DTS 源文件。";
            }
            QueueOutlineRefresh(clear: true);
            return;
        }
        Status.Text = "正在准备代码提示…";
        try
        {
            if (project.Kind == ProjectKind.CubeMx && !await ConfigureCubeMxAsync(directory, token))
            {
                Status.Text = "工程已打开；CMake 配置失败，请查看构建日志并修正工程配置。";
                return;
            }
            Status.Text = "正在准备代码索引与提示…";
            await services.Intelligence.StartAsync(directory, token);
            QueueLiveDiagnostics();
            Status.Text = IsStcSdccProject ? "已打开 " + project.Name + " · 通用 C 代码提示已就绪；8051 扩展语义以 SDCC 编译为准"
                : "已打开 " + project.Name + " · " + services.Intelligence.StatusDescription;
            QueueOutlineRefresh(clear: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(ex.ToString()); Status.Text = "工程已打开；代码提示不可用：" + ex.Message; }
    }

    private async void CloseProject_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (await CloseProjectAsync(token))
        {
            ShowDocument(WelcomeTab);
        }
    });

    private async Task<bool> CloseProjectAsync(CancellationToken token, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        CancelFreeRtosRead();
        if (projectDirectory is null)
        {
            return true;
        }
        token.ThrowIfCancellationRequested();
        if (!await ConfirmDocumentsAsync(decide))
        {
            Status.Text = "已取消关闭工程。";
            return false;
        }
        token.ThrowIfCancellationRequested();
        var wasEnabled = WorkspaceTabs.IsEnabled;
        WorkspaceTabs.IsEnabled = false;
        try
        {
            await projectTransitions.StopCurrentAsync(token);
            CancelProjectTreeLoading();
            CancelBuildMemoryRefresh();
            ClearEditorDocuments();
            projectDirectory = null;
            ResetFirstProjectEvidence();
            LvglPreview.SetProject(null);
            ClearHdlSchematic();
            ResetAiForProjectChange();
            RefreshSkillsForProjectChange();
            GitGraph.SetProject(null);
            GitHubWorkspace.SetProject(null);
            BuildMemory.SetMessage("打开工程并编译后显示占用。");
            SetProjectDetailsMode(null);
            PackagesTab.Visibility = Visibility.Collapsed;
            await ProjectTerminal.SetProjectAsync(null);
            ApplyDownloadConfiguration(null);
            explorerMenuEntry = null;
            explorerMouseContext = false;
            contextOffset = null;
            contextFromMouse = false;
            ProjectTree.Items.Clear();
            ProjectTree.Visibility = Visibility.Collapsed;
            EmptyProject.Visibility = Visibility.Visible;
            ProjectLabel.Text = "工作区";
            DeviceLabel.Text = "器件未选择";
            ToolsetLabel.Text = "";
            WindowProjectTitle.Text = "欢迎";
            Title = "MCU StudioX";
            BuildConfiguration.Text = "无构建目标";
            EditorBreadcrumb.Text = "";
            EditorBreadcrumb.ToolTip = null;
            EditorLanguage.Text = "";
            StatusLanguage.Text = "";
            StatusEncoding.Text = "";
            EditorPosition.Text = "";
            BuildLog.Clear();
            UpdateProjectActions(busy: false);
            Status.Text = "已关闭工程。";
            return true;
        }
        finally { WorkspaceTabs.IsEnabled = wasEnabled; }
    }

    private async Task BeginNewProjectAsync(CancellationToken token, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        if (!await CloseProjectAsync(token, decide))
        {
            return;
        }
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        PackageStatus.Text = "正在读取器件目录…";
        await RefreshPacksAsync(token);
        SelectPack(null);
        DeviceSearch.Clear();
        ProjectName.Text = "my_firmware";
        Status.Text = "请选择器件厂商，创建新工程。";
    }

    private void UpdateProjectActions(bool busy)
    {
        projectActionsBusy = busy;
        var available = projectDirectory is not null && !busy;
        VerifyProjectComponentsButton.IsEnabled = available;
        BuildButton.IsEnabled = BuildMenu.IsEnabled = available && !IsZephyrProject && !IsMicroPythonProject;
        CloseProjectMenu.IsEnabled = CloseProjectButton.IsEnabled = available;
        DownloadButton.IsEnabled = DownloadMenu.IsEnabled = available && supportsDownload && (!IsMicroPythonProject || !MicroPythonPanel.IsBusy);
        MicroPythonRunButton.Visibility = IsMicroPythonProject ? Visibility.Visible : Visibility.Collapsed;
        MicroPythonRunButton.IsEnabled = available && IsMicroPythonProject && !MicroPythonPanel.IsBusy;
        MicroPythonRunLabel.Text = MicroPythonPanel.IsScriptRunning ? "运行中…" : "开始运行";
        DownloadProbePicker.IsEnabled = available && supportsDownload && !IsStcSdccProject && !IsEspressifProject && !IsZephyrProject && !IsMicroPythonProject;
        DownloadSettingsButton.IsEnabled = DownloadSettingsMenu.IsEnabled = available && supportsDownload;
        CancelButton.IsEnabled = !GitGraph.IsMutating && (operationCancellation is not null || buildMemoryCancellation is not null ||
            IsMicroPythonProject && (MicroPythonPanel.IsBusy || services.MicroPython.IsConnected));
        UpdateStcIspControls();
        UpdateDebugControls();
        RefreshAg32LogicUi(currentProjectManifest, busy);
        RefreshPluginCommandState();
    }
}

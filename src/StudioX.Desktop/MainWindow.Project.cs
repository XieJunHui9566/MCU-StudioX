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
        // 官方 SDK 示例保留原文件名和目录；由创建记录指定入口，旧工程继续使用原有默认位置。
        var mainPath = project.EntryFile ?? (project.Kind == ProjectKind.CubeMx ? "Core/Src/main.c" : "src/main.c");
        var source = services.Files.FileExists(directory, mainPath)
            ? await services.Files.ReadAsync(directory, mainPath, token)
            : null;
        ClearEditorDocuments();
        projectDirectory = directory;
        LvglPreview.SetProject(directory);
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
        BuildConfiguration.Text = project.Name + " · " + (project.CubeMx?.ConfigurePreset ?? project.CubeMx?.BuildType ?? "Debug");
        DeviceLabel.Text = "器件 / " + project.DeviceId;
        ToolsetLabel.Text = $"工具集 / {project.ToolsetId} {project.ToolsetVersion}";
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
        PopulateProjectTree(project.Name);
        await RefreshBuildMemoryAsync(directory, token);
        await services.RecentProjects.RememberAsync(project.Name, directory, token);
        await RefreshRecentAsync(token);
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
            ClearEditorDocuments();
            projectDirectory = null;
            LvglPreview.SetProject(null);
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
        BuildButton.IsEnabled = BuildMenu.IsEnabled = available;
        CloseProjectMenu.IsEnabled = CloseProjectButton.IsEnabled = available;
        DownloadButton.IsEnabled = DownloadMenu.IsEnabled = available && supportsDownload;
        DownloadProbePicker.IsEnabled = available && supportsDownload && !IsStcSdccProject && !IsEspressifProject;
        DownloadSettingsButton.IsEnabled = DownloadSettingsMenu.IsEnabled = available && supportsDownload;
        UpdateStcIspControls();
        UpdateDebugControls();
        RefreshAg32LogicUi(currentProjectManifest, busy);
    }
}

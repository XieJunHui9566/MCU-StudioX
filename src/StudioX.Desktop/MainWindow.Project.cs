namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class MainWindow
{
    private async void CloseProject_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (await CloseProjectAsync(token)) ShowDocument(WelcomeTab);
    });

    private async Task<bool> CloseProjectAsync(CancellationToken token, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        CancelFreeRtosRead();
        if (projectDirectory is null) return true;
        token.ThrowIfCancellationRequested();
        if (!await ConfirmDocumentsAsync(decide)) { Status.Text = "已取消关闭工程。"; return false; }
        token.ThrowIfCancellationRequested();
        await DisposeAiMcpSessionAsync();
        token.ThrowIfCancellationRequested();
        var wasEnabled = WorkspaceTabs.IsEnabled;
        WorkspaceTabs.IsEnabled = false;
        try
        {
            await PersistBreakpointLinesAsync();
            await services.Debugger.OpenProjectAsync(null, token);
            await debugNavigationTask;
            debugAnchors.Clear(); lastDebugSnapshot = null;
            DebugTools.ClearBreakpointLog();
            HideDebugLayout();
            // 全部保存决定确认后统一关闭，等待旧请求结束，避免回调把旧工程内容重新显示出来。
            CloseCodeAssistance();
            if (sourceContextMenu is not null) sourceContextMenu.IsOpen = false;
            if (explorerMenu is not null) explorerMenu.IsOpen = false;
            await assistTask;
            await Task.WhenAll(hoverTask, navigationTask);
            await services.Intelligence.StopAsync();
            ClearEditorDocuments();
            projectDirectory = null;
            ResetAiForProjectChange();
            RefreshSkillsForProjectChange();
            GitGraph.SetProject(null);
            GitHubWorkspace.SetProject(null);
            BuildMemory.SetMessage("打开工程并编译后显示占用。");
            SetProjectDetailsMode(null);
            PackagesTab.Visibility = Visibility.Collapsed;
            await ProjectTerminal.SetProjectAsync(null);
            ApplyDownloadConfiguration(null);
            explorerMenuEntry = null; explorerMouseContext = false;
            contextOffset = null; contextFromMouse = false;
            ProjectTree.Items.Clear(); ProjectTree.Visibility = Visibility.Collapsed;
            EmptyProject.Visibility = Visibility.Visible;
            ProjectLabel.Text = "工作区"; DeviceLabel.Text = "器件未选择"; ToolsetLabel.Text = "";
            WindowProjectTitle.Text = "欢迎"; Title = "MCU StudioX";
            BuildConfiguration.Text = "无构建目标";
            EditorBreadcrumb.Text = ""; EditorBreadcrumb.ToolTip = null;
            EditorLanguage.Text = ""; EditorPosition.Text = "";
            BuildLog.Clear();
            UpdateProjectActions(busy: false);
            Status.Text = "已关闭工程。";
            return true;
        }
        finally { WorkspaceTabs.IsEnabled = wasEnabled; }
    }

    private async Task BeginNewProjectAsync(CancellationToken token, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        if (!await CloseProjectAsync(token, decide)) return;
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        PackageStatus.Text = "正在读取器件目录…";
        await RefreshPacksAsync(token);
        SelectPack(null); DeviceSearch.Clear(); ProjectName.Text = "my_firmware";
        Status.Text = "请选择器件厂商，创建新工程。";
    }

    private void UpdateProjectActions(bool busy)
    {
        projectActionsBusy = busy;
        var available = projectDirectory is not null && !busy;
        BuildButton.IsEnabled = BuildMenu.IsEnabled = available;
        CloseProjectMenu.IsEnabled = CloseProjectButton.IsEnabled = available;
        DownloadButton.IsEnabled = DownloadMenu.IsEnabled = available && supportsDownload;
        DownloadProbePicker.IsEnabled = available && supportsDownload && !IsStcSdccProject;
        DownloadSettingsButton.IsEnabled = DownloadSettingsMenu.IsEnabled = available && supportsDownload;
        UpdateStcIspControls();
        UpdateDebugControls();
        RefreshAg32LogicUi(currentProjectManifest, busy);
    }
}

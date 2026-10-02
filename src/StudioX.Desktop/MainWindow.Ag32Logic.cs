namespace StudioX.Desktop;

using System.Windows;
using Microsoft.Win32;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    private string? ag32MappingDetailsProject;
    private string? ag32MappingDetails;
    private bool ag32MappingSourceMissing;

    private void RefreshAg32LogicUi(ProjectManifest? project, bool busy = false)
    {
        var supported = project is not null && Ag32DeviceCatalog.Find(project.DeviceId)?.CanMap == true;
        var custom = project?.Logic is not null;
        HdlSchematic.SetBusy(busy);
        HdlWorkflow.SetBusy(busy);
        if (!supported || !custom) ClearHdlSchematic();
        var mapped = project?.PinMapping is not null;
        var visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        Ag32PinMappingRailButton.Visibility = BuildLogicButton.Visibility = DownloadLogicButton.Visibility = visibility;
        Ag32PinMappingRailButton.IsEnabled = supported && projectDirectory is not null && !busy;
        BuildLogicButton.IsEnabled = DownloadLogicButton.IsEnabled = supported && (mapped || custom) && !busy;
        BuildLogicButton.Header = custom ? "编译 AG32 固件与自定义逻辑" : "编译 AG32 引脚映射";
        DownloadLogicButton.Header = custom ? "下载 AG32 固件与自定义逻辑" : "下载 AG32 固件与映射";
        var details = !supported ? "打开已适配的 AG32 工程后配置。"
            : custom ? "已启用自定义 Verilog。顶部编译会运行内置原生综合与布局布线；构建配置与波形仿真可从下方打开。"
            : mapped ? "已启用基础映射。修改 .ve 后须重新编译并下载，映射才会写入芯片。"
            : "现有工程尚未启用基础映射。启用时保留已有 logic/pins.ve；不存在时建立模板。";
        Ag32PinMapping.SetState(mapped, custom, busy,
            projectDirectory == ag32MappingDetailsProject && ag32MappingDetails is not null && !custom
                ? ag32MappingDetails : details,
            projectDirectory == ag32MappingDetailsProject && ag32MappingSourceMissing);
        if (!supported)
        {
            Ag32PinMappingTab.Visibility = Visibility.Collapsed;
            ClearAg32PinPlan();
        }
    }

    private async void Ag32PinMappingRail_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await ShowAg32PinMappingAsync(token);
        if (currentProjectManifest?.Logic is not null)
        {
            await OpenHdlWorkflowAsync(token);
        }
    });

    private async Task ShowAg32PinMappingAsync(CancellationToken token)
    {
        Ag32PinMappingTab.Visibility = Visibility.Visible;
        ShowDocument(Ag32PinMappingTab);
        await RefreshAg32PinMappingStatusAsync(token);
    }

    private async Task RefreshSelectedAg32PinMappingAsync()
    {
        if (!Ag32PinMappingTab.IsSelected || projectActionsBusy || closing || closed ||
            currentProjectManifest is null || Ag32DeviceCatalog.Find(currentProjectManifest.DeviceId)?.CanMap != true)
        {
            return;
        }
        try
        {
            await RefreshAg32PinMappingStatusAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            Log("AG32 映射页状态读取失败：" + error);
        }
    }

    /// <summary>启动参数只展示映射页，不自动启用、编译或访问硬件。</summary>
    public Task ShowAg32PinMappingPageAsync() => RunAsync(async token =>
    {
        if (currentProjectManifest is null || Ag32DeviceCatalog.Find(currentProjectManifest.DeviceId)?.CanMap != true)
        {
            throw new StudioX.Foundation.StudioXException("AG32_MAPPING_DEVICE", "请先打开已适配的 AG32 工程。");
        }
        await ShowAg32PinMappingAsync(token);
    });

    private async Task RefreshAg32PinMappingStatusAsync(CancellationToken token)
    {
        var root = RequireProject();
        var project = await ProjectService.ReadAsync(root, token);
        if (projectDirectory != root)
        {
            return;
        }
        currentProjectManifest = project;
        RefreshAg32LogicUi(project, projectActionsBusy);
        if (project.Logic is not null)
        {
            return;
        }
        var status = await services.Ag32PinMapping.InspectAsync(root, token);
        if (projectDirectory == root)
        {
            var details = $"源文件：{status.SourcePath}\n映射镜像：{status.ImagePath}\n构建凭据："
                + (status.ReceiptCurrent ? "与当前源码及工具一致" : "尚未构建或已失效，请重新编译")
                + "\nSupra 许可：" + (status.LicenseConfigured ? "本机已配置；有效性由实际编译检查" : "本机尚未配置")
                + (status.Diagnostics.Length == 0 ? "" : "\n\n" + string.Join("\n", status.Diagnostics));
            if (project.PinMapping is { } mapping && FindEditor(mapping.PinMapFile)?.IsDirty == true)
            {
                details += "\n\n.ve 有未保存编辑；凭据检查针对磁盘内容，保存后须重新编译。";
            }
            ag32MappingDetailsProject = root;
            ag32MappingDetails = details;
            ag32MappingSourceMissing = status.Enabled && !File.Exists(status.SourcePath);
            Ag32PinMapping.SetState(status.Enabled, false, projectActionsBusy, details, ag32MappingSourceMissing);
            if (status.Enabled && !ag32MappingSourceMissing)
            {
                await RefreshAg32PinPlanAsync(root, token);
            }
        }
    }

    private async void EnableAg32PinMapping_Click(object? sender, EventArgs e) => await RunAsync(async token =>
    {
        var root = RequireProject();
        await SaveAllSourcesAsync(root, token);
        currentProjectManifest = await ProjectService.EnableAg32PinMappingAsync(root, token);
        var mappingPath = currentProjectManifest.PinMapping!.PinMapFile;
        if (FindEditor(mappingPath) is { } editor)
        {
            EditorSynchronizer.Apply(editor, await services.Files.ReadAsync(root, mappingPath, token));
        }
        RefreshProjectTree();
        await RefreshAg32PinMappingStatusAsync(token);
        await OpenSourceAsync(mappingPath, token);
        Status.Text = "基础映射已启用，VE 已打开；普通编译和下载会处理映射镜像。";
    });

    private async void OpenAg32PinMapping_Click(object? sender, EventArgs e) => await RunAsync(async token =>
    {
        var project = await ProjectService.ReadAsync(RequireProject(), token);
        if (project.PinMapping is { } mapping)
        {
            await OpenSourceAsync(mapping.PinMapFile, token);
        }
        else if (project.Logic is { } logic)
        {
            await OpenSourceAsync(logic.PinMapFile, token);
        }
    });

    private async void ConfigureAg32PinMappingLicense_Click(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择厂商提供的 Supra 许可文件", Filter = "Supra 许可|license.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunAsync(async token =>
        {
            await services.Ag32PinMapping.ImportLicenseAsync(dialog.FileName, token);
            await RefreshAg32PinMappingStatusAsync(token);
            Status.Text = "Supra 许可已保存到本机用户目录；许可内容不会进入工程、运行时或日志。";
        });
    }

    private async void OpenAg32SupraDownload_Click(object? sender, EventArgs e) => await RunAsync(_ =>
    {
        Ag32PinMappingDocumentation.OpenDownload();
        return Task.CompletedTask;
    });

    private async void OpenAg32SupraLicenseGuide_Click(object? sender, EventArgs e) => await RunAsync(_ =>
    {
        Ag32PinMappingDocumentation.OpenLicenseInstructions();
        return Task.CompletedTask;
    });

    private async void BuildLogic_Click(object sender, RoutedEventArgs e) => await BuildAg32PinMappingAsync();

    private async Task BuildAg32PinMappingAsync() => await RunAsync(async token =>
    {
        var root = RequireProject();
        await SaveAllSourcesAsync(root, token);
        var project = await ProjectService.ReadAsync(root, token);
        if (project.Logic is not null)
        {
            var joint = await BuildWithSummaryAsync(root, token);
            Log(joint.Summary);
            Status.Text = joint.Summary;
            return;
        }
        if (project.PinMapping is null)
        {
            await ShowAg32PinMappingAsync(token);
            return;
        }
        ShowBottom(0);
        BuildLog.Clear();
        try
        {
            var report = await services.Ag32PinMapping.BuildAsync(root,
                new Progress<string>(text => { Status.Text = text; Log(text); }), token,
                new Progress<string>(text => Log(text.TrimEnd('\r', '\n'))));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Log("映射构建日志：" + report.LogPath);
            Status.Text = report.Success ? "映射编译成功；尚未下载到芯片。" : "映射编译失败，请查看原始诊断。";
            Log(Status.Text);
        }
        finally
        {
            if (projectDirectory == root)
            {
                try { await RefreshAg32PinMappingStatusAsync(CancellationToken.None); }
                catch (Exception error) { Log("时序状态刷新失败：" + error); }
            }
        }
    });

    private void DownloadLogic_Click(object sender, RoutedEventArgs e) => Download_Click(sender, e);
}

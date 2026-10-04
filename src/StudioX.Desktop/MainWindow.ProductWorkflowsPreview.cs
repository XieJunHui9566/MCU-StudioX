namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>使用隔离用户目录和实际编译快照检查页面；不启动硬件或安装目录中的工具。</summary>
    public async Task RenderProductWorkflowsPreviewAsync(string directory, string catalog, string snapshotsFile)
    {
        List<string> checks = [];
        void Check(bool condition, string detail)
        {
            if (!condition)
            {
                throw new InvalidOperationException(detail);
            }
            checks.Add(detail);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        Width = 1440;
        Height = 960;
        await ShowDistributionAsync();
        var listing = await services.Distribution.ReadAsync(catalog);
        distributionView!.SetListing(listing);
        Check(distributionView.VisibleEntryCount == 2, "离线目录显示插件和组件，不触发下载或安装");
        distributionView.SetFilter("byte-utils", 3);
        Check(distributionView.VisibleEntryCount == 1, "目录按组件类型和名称筛选");
        distributionView.SelectFirst();
        Check(distributionView.DetailText.Contains("MIT") && distributionView.DetailText.Contains("SHA-256") && distributionView.DetailText.Contains("磁盘"), "选中条目显示许可、来源、哈希和磁盘预估");
        distributionView.SetFilter("不存在", 0);
        Check(distributionView.VisibleEntryCount == 0, "目录搜索无结果为空列表");
        distributionView.SetFilter("", 0);
        distributionView.SelectFirst();
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Settle();
            Render(this, Path.Combine(directory, "distribution-" + theme.Id + ".png"));
        }
        await CloseWorkspaceTabAsync(distributionTab!);
        await ShowDistributionAsync();
        Check(distributionTab!.Visibility == Visibility.Visible && distributionView.Listing == listing, "关闭并重开目录标签保留当前选择");
        ApplyTheme(ThemeService.Dark);
        Width = MinWidth;
        Height = 720;
        await Settle();
        Check(distributionView.ListHasSpace, "最小窗口保留目录列表及详情空间，操作区可滚动");
        Render(this, Path.Combine(directory, "distribution-small.png"));
        Width = 1440;
        Height = 960;
        await ShowFaultAnalysisAsync();
        var report = FaultAnalyzer.Analyze(new(1, "STM32F407ZG", "离线界面示例，未连接开发板", DateTimeOffset.UtcNow,
            1u << 9 | 1u << 15 | 1u << 25, 1u << 30, null, 0x20020000, 0x08000100, null, "构造输入：CFSR/HFSR/BFAR，非实板故障"));
        faultAnalysis!.SetReport(report);
        Check(faultAnalysis.ResultText.Contains("DIVBYZERO") && faultAnalysis.ResultText.Contains("PRECISERR") && faultAnalysis.Report == report, "故障工作台呈现状态位与原始证据");
        await Settle();
        Render(this, Path.Combine(directory, "fault-analysis.png"));
        await CloseWorkspaceTabAsync(faultAnalysisTab!);
        await ShowFaultAnalysisAsync();
        Check(faultAnalysisTab!.Visibility == Visibility.Visible && faultAnalysis.Report == report, "故障标签重开保留分析报告");
        buildHistoryView = new();
        buildHistoryTab = AddToolTab("构建历史与对比", buildHistoryView);
        ShowDocument(buildHistoryTab);
        var snapshots = await JsonStore.ReadAsync<BuildHistorySnapshot[]>(snapshotsFile);
        buildHistoryView.SetSnapshots(snapshots);
        buildHistoryView.Compare();
        Check(buildHistoryView.ComparisonCount > 0 && buildHistoryView.DescriptionText.Contains("开发环境组件版本"), "实际两次编译快照在界面呈现比较与配置限制");
        await Settle();
        Render(this, Path.Combine(directory, "build-comparison.png"));
        buildHistoryView.SetSnapshots([]);
        Check(buildHistoryView.DescriptionText.Contains("需要两次") && buildHistoryView.ComparisonCount == 0, "无构建历史时明确提示并清除旧比较");
        try
        {
            await services.DebugLaunch.StartAsync(Path.Combine(directory, "missing-project"), false);
        }
        catch (Exception) { /* 缺少工程是界面验证输入；原始错误由应用服务保留。 */ }
        var connection = new DebugConnectionWindow(services.DebugLaunch, "离线连接记录示例：本地准备失败，未访问探针", false,
            (_, _) => throw new InvalidOperationException("预览禁止连接"), review: true)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        connection.Show();
        await Settle();
        connection.UpdateLayout();
        Render(connection, Path.Combine(directory, "debug-connection.png"));
        Check(services.DebugLaunch.Current.Steps[0].Status == "失败" && !services.Debugger.IsActive, "连接记录标明失败阶段，预览不连接目标");
        connection.Close();
        Check(!File.Exists(Path.Combine(services.DataDirectory, "plugins.json")), "读取目录和界面预览未授予插件执行信任");
        foreach (var id in new[] { "debug-connection", "fault-analysis", "build-comparison", "distribution", "components" })
        {
            Check(services.Help.Get(id).Markdown.Length > 500, "离线帮助已包含完整操作步骤：" + id);
        }
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            success = true,
            hardware = false,
            installation = false,
            checks
        });
    }
}

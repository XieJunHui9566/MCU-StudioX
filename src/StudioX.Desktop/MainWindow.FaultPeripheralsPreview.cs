namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Engine.Svd;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>离线界面验收：构造明确标记的读数，不启动探针或语言工具。</summary>
    public async Task RenderFaultPeripheralsPreviewAsync(string output, string svd)
    {
        var checks = new List<string>();
        void Check(bool passed, string label)
        {
            if (!passed)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        var device = await SvdParser.LoadAsync(svd);
        var view = new PeripheralView();
        var tab = AddToolTab("外设寄存器 · 离线界面示例", view);
        ShowDocument(tab);
        view.SetDocument(new(output, new(1, "离线界面示例", "fixture.svd", device.Sha256), device));
        var reg = device.Registers.First();
        view.SelectRegister(reg.Path);
        Check(view.DetailText.Contains("不是当前值", StringComparison.Ordinal), "reset value is explicitly not a current reading");
        view.SetReading(new(reg.Path, 1, DateTimeOffset.UtcNow, 0));
        Check(view.DetailText.Contains("当前值：0x1", StringComparison.Ordinal), "explicit reading renders register and field values");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await Settle();
            Check(view.ActualWidth > 700 && view.ActualHeight > 400, theme.Id + " has usable register and field area");
            Render(this, Path.Combine(output, "peripherals-" + theme.Id + ".png"));
        }
        view.ClearReading("离线示例：目标已运行");
        Check(!view.DetailText.Contains("当前值：0x1", StringComparison.Ordinal), "resume removes stale values");
        view.SetDocument(null);
        Check(view.Selected is null && !view.DetailText.Contains("当前值：0x1", StringComparison.Ordinal), "project/document removal clears stale selection");
        await ShowFaultAnalysisAsync();
        var dump = new CoreDumpEvidence(new string('a', 64), "b64", "esp32s3", "5.5.4", "1.15.0", new string('b', 64), true, [new("crashed_task", 0x3fc80000, 0x3fc81000, 1024, 0)], "crashed_task", "Offline preview only", "用户选择的归档 ELF");
        var report = new FaultAnalysisReport(new(1, "ESP32-S3", "离线界面示例；模拟报告，未连接设备", DateTimeOffset.UtcNow, null, null, null, null, null, null, "模拟原始输出"), ["仅测试报告布局。"], [], "示例调用栈", new string('b', 64), dump);
        faultAnalysis!.SetReport(report);
        Check(faultAnalysis.ResultText.Contains("转储 SHA-256", StringComparison.Ordinal) && faultAnalysis.ResultText.Contains("crashed_task", StringComparison.Ordinal), "core dump provenance and tasks visible");
        Width = MinWidth;
        Height = 720;
        ApplyTheme(ThemeService.Dark);
        await Settle();
        Render(this, Path.Combine(output, "fault-compact.png"));
        faultAnalysis.SetDiagnostic("离线界面示例错误");
        Check(faultAnalysis.Report is null, "failed decoding clears old exportable report");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            hardware = false,
            checks
        });
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}

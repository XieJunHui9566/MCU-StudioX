namespace StudioX.Desktop;

using System.Windows.Controls;
using Microsoft.Win32;

public partial class MainWindow
{
    private FaultAnalysisView? faultAnalysis;
    private TabItem? faultAnalysisTab;
    private Task ShowFaultAnalysisAsync()
    {
        if (faultAnalysisTab is null)
        {
            faultAnalysis = new()
            {
                Requested = RunFaultAnalysisAsync
            };
            faultAnalysisTab = AddToolTab("固件故障分析", faultAnalysis);
        }
        ShowDocument(faultAnalysisTab);
        return Task.CompletedTask;
    }
    private Task RunFaultAnalysisAsync(string action) => RunAsync(async token =>
    {
        var view = faultAnalysis!;
        try
        {
            if (action == "analyze")
            {
                view.SetReport(services.Faults.Analyze(currentProjectManifest?.DeviceId ?? "未指定", view.Input));
            }
            else if (action == "read")
            {
                view.SetReport(await services.Faults.ReadPausedAsync(token));
            }
            else if (action == "locate" && view.Report is { } report)
            {
                view.SetReport(await services.Faults.LocateAsync(RequireProject(), report, token));
            }
            else if (action == "export" && view.Report is { } exported)
            {
                var dialog = new SaveFileDialog { Filter = "故障报告|*.json", FileName = "studiox-fault-report.json" };
                if (dialog.ShowDialog(this) == true)
                {
                    await services.Faults.ExportAsync(exported, dialog.FileName, token);
                }
            }
            else if (action is "import" or "dump" or "archive-dump")
            {
                var dialog = new OpenFileDialog { Filter = action == "import" ? "故障报告|*.json" : "Base64 转储|*.b64;*.txt|ELF 转储|*.elf|原始转储|*.bin;*.raw" };
                if (dialog.ShowDialog(this) != true)
                {
                    return;
                }
                if (action == "import")
                {
                    view.SetReport(await services.Faults.ImportAsync(dialog.FileName, token));
                }
                else
                {
                    var type = dialog.FilterIndex == 1 ? "b64" : dialog.FilterIndex == 2 ? "elf" : "raw";
                    if (action == "archive-dump")
                    {
                        var elf = new OpenFileDialog { Title = "选择故障固件对应的归档 ELF", Filter = "应用程序 ELF|*.elf" };
                        if (elf.ShowDialog(this) != true)
                        {
                            return;
                        }
                        view.SetReport(await services.Faults.DecodeDumpAsync(RequireProject(), dialog.FileName, type, elf.FileName, token));
                    }
                    else
                    {
                        view.SetReport(await services.Faults.DecodeDumpAsync(RequireProject(), dialog.FileName, type, token));
                    }
                }
            }
        }
        catch (Exception error) { view.SetDiagnostic(error.ToString()); throw; }
    });
}

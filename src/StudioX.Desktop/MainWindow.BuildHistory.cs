namespace StudioX.Desktop;

using System.Windows.Controls;
using Microsoft.Win32;

public partial class MainWindow
{
    private BuildHistoryView? buildHistoryView;
    private TabItem? buildHistoryTab;
    private Task ShowBuildHistoryAsync() => RunAsync(async token =>
    {
        if (buildHistoryTab is null)
        {
            buildHistoryView = new()
            {
                Requested = RunBuildHistoryAsync
            };
            buildHistoryTab = AddToolTab("构建历史与对比", buildHistoryView);
        }
        ShowDocument(buildHistoryTab);
        buildHistoryView!.SetSnapshots(await services.BuildHistory.ListAsync(RequireProject(), token));
    });
    private Task RunBuildHistoryAsync(string action) => RunAsync(async token =>
    {
        if (action == "capture")
        {
            await services.BuildHistory.CaptureAsync(RequireProject(), token: token);
        }
        if (action is "refresh" or "capture")
        {
            buildHistoryView!.SetSnapshots(await services.BuildHistory.ListAsync(RequireProject(), token));
        }
        else if (action == "compare")
        {
            buildHistoryView!.Compare();
        }
        else if (action == "export" && buildHistoryView!.Before is { } before && buildHistoryView.After is { } after)
        {
            var dialog = new SaveFileDialog { Filter = "构建对比|*.json", FileName = "studiox-build-comparison.json" };
            if (dialog.ShowDialog(this) == true)
            {
                await services.BuildHistory.ExportAsync(before, after, dialog.FileName, token);
            }
        }
    });
}

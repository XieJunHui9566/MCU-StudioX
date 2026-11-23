namespace StudioX.Desktop;

using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private async Task StartTeachingDebugAsync(CancellationToken token)
    {
        var root = RequireProject();
        if (!TeachingSimulatorTarget.Supports(currentProjectManifest))

        {
            throw new StudioXException("TEACHING_TARGET", "此工程型号尚未接入示教平台调试。");
        }
        if (loadedBuildSettings is not null &&
            (!TrySelectedBuildSettings(out var selected, out var error) || selected != loadedBuildSettings))
        {
            FocusStcBuildSettings();
            BuildSettingsStatus.Text = Status.Text = error.Length > 0 ? error : "编译参数尚未保存；请先保存后再调试。";
            return;
        }
        await SaveAllSourcesAsync(root, token);
        await PersistBreakpointLinesAsync();
        ApplyBuildSettings(await services.StcBuilds.PrepareTeachingDebugAsync(root, token));
        ShowBottom(0);
        BuildLog.Clear();
        Log("示教平台调试：需要在本电脑运行单片机示教平台；不会连接实物串口。");
        var build = await BuildWithSummaryAsync(root, token);
        Log(build.Summary);
        if (!build.Success)
        {
            throw new StudioXException("TEACHING_BUILD", "最新源码编译失败，未覆盖模拟程序。\n" + build.Summary);
        }
        if (editorDocuments.Any(document => document.IsDirty))

        {
            throw new StudioXException("MON51_SOURCE_CHANGED", "编译期间出现未保存编辑，请保存后重新调试。");
        }
        await services.Debugger.StartTeachingSimulatorAsync(token);
        RefreshDebugUi();
        Status.Text = services.Debugger.Reason;
    }
}

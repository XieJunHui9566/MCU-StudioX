namespace StudioX.Desktop;

using System.Windows.Controls;
using StudioX.Application.Editing;
using StudioX.Application.StcTiming;
using StudioX.Foundation;

public partial class MainWindow
{
    private void InitializeStcTiming()
    {
        const string title = "51 延时与波特率…";
        bool Enabled() => projectDirectory is not null && IsStcSdccProject;
        workbenchCommands.Add(new(title, "", Enabled, ShowStcTimingAsync));
        ProjectToolsMenu.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(ShowStcTimingAsync, Enabled) });
        sourceContextMenu?.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(ShowStcTimingAsync, Enabled) });
    }

    private async Task ShowStcTimingAsync()
    {
        if (projectDirectory is not { } project)
        {
            return;
        }
        var target = CaptureTemplateTarget(start: 0, length: 0);
        var insertable = target is not null && !services.Debugger.IsActive &&
            Path.GetExtension(target.Session.Source.RelativePath).Equals(".c", StringComparison.OrdinalIgnoreCase);
        CloseCodeAssistance();
        try
        {
            var context = await services.StcTiming.ReadAsync(project);
            if (closing || project != projectDirectory)
            {
                return;
            }
            var window = new StcTimingWindow(services.StcTiming, context, insertable, Log) { Owner = this };
            if (window.ShowDialog() == true && window.Result is { } preview && target is not null)
            {
                await ApplyStcTimingAsync(target, window.Context, preview);
            }
        }
        catch (Exception ex)
        {
            Status.Text = "51 计时生成：" + ex.Message;
            Log(ex.ToString());
        }
    }

    private async Task ApplyStcTimingAsync(TemplateInsertionTarget target, StcTimingContext context, StcTimingPreview preview)
    {
        await services.StcTiming.ValidateAsync(context);
        if (!TemplateTargetCurrent(target) || context.ProjectDirectory != projectDirectory || services.Debugger.IsActive)
        {
            throw new StudioXException("STC_TIMING_STALE", "文件、工程或编辑状态已变化，请重新打开 51 计时生成窗口。");
        }
        var code = services.StcTiming.PrepareInsertion(preview, target.Document.Text);
        if (ApplyCodeTemplate(target, new CodeTemplateExpansion(code, code.Length)))
        {
            QueueEditorCheckpoint();
            Status.Text = "已插入 " + preview.FunctionName + " · Ctrl+Z 可整段撤销；请调用函数并检查返回值。";
        }
    }
}

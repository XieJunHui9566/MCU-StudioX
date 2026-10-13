namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using StudioX.Application;

public partial class MainWindow
{
    private async Task OpenPluginProjectAsync(string pluginId, JsonElement target, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        if (!CanRunPlugin(pluginId) || FindPluginSession(pluginId) is not { IsApplicationSession: true } session)
        {
            return;
        }
        await RunAsync(async token =>
        {
            var plan = await services.PluginProjectNavigation.PrepareAsync(target, token);
            if (!ReferenceEquals(session, pluginApplication) || !session.IsPluginRunning(pluginId))
            {
                return;
            }
            await OpenProjectAsync(plan.Directory, token, decide);
            // 用户可能取消当前脏文档的关闭；不能把迁移日志发布到仍然打开的旧工程。
            if (!string.Equals(projectDirectory, plan.Directory, StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = "已取消打开移植工程；移植副本和当前编辑内容均保留。";
                return;
            }
            if (plan.Diagnostic is not null)
            {
                Log("移植编译记录不可用于错误标记：" + plan.Diagnostic);
            }
            if (plan.BuildOutput is not null)
            {
                ClearBuildDiagnostics();
                Log("移植时的原始编译输出（当前构建请使用 F7）：\n" + plan.BuildOutput);
                await PublishBuildDiagnosticsAsync(plan.Directory, plan.BuildOutput, diagnosticRevision, token, plan.SourceHashes, "移植编译");
                ShowBottom(buildDiagnostics.Count > 0 ? 4 : 0);
            }
        });
    }
}

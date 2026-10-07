namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application.Plugins;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离用户数据中验证真实独立宿主和欢迎页 UI，不连接设备。</summary>
    public async Task RenderApplicationPluginsPreviewAsync(string directory, string fixtureArchive, string keilArchive)
    {
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Layout()
        {
            FlushPluginPanels();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            FlushPluginPanels();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        JsonElement Empty() => JsonSerializer.SerializeToElement(new
        {
        });
        int Count(PluginWorkspaceSession session, string id) => int.Parse(session.LatestPanels[id + "/status"].Widgets
            .Single(widget => widget.Id == "count").Value!.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var installed = await services.PluginManager.ImportAsync(fixtureArchive);
        Check(installed.Manifest?.Scope == "application" && !installed.Enabled, "应用插件安装后不自动信任");
        await services.PluginManager.SetEnabledAsync(installed.Id, true);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        var application = pluginApplication!;
        Check(projectDirectory is null && pluginWorkspace is null && application.IsApplicationSession && application.Project is null,
            "欢迎页建立无工程的应用会话");
        Check(CanRunPlugin(installed.Id) && application.Contributions.Count == 1 && pluginActivities.ContainsKey(installed.Id),
            "欢迎页命令和独立入口可用");
        ShowDocument(pluginActivities[installed.Id].Tab);
        await Layout();
        var originalPanel = pluginPanels[installed.Id + "/status"];
        var input = PluginDescendants(originalPanel.Host).OfType<TextBox>().Single();
        input.Text = "保留迁移输入";
        await InvokePluginCommandAsync(installed.Id, "increment", Empty());
        await Layout();
        Check(Count(application, installed.Id) == 1 && PluginDescendants(originalPanel.Host).OfType<TextBox>().Single().Text == "保留迁移输入",
            "欢迎页真实命令往返宿主并保留表单输入");
        try
        {
            await application.InvokeAsync(installed.Id, "command", "forbidden", Empty());
            throw new InvalidOperationException("工程工具未拒绝");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_HOST_TOOL" || error.Message.Contains("PLUGIN_HOST_TOOL", StringComparison.Ordinal))
        {
            Check(true, "无工程应用会话拒绝 project_info 宿主工具");
        }

        var source = Path.Combine(directory, "scope-fixture");
        Directory.CreateDirectory(source);
        var manifest = installed.Manifest!;
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(installed.ManifestPath)!, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, Path.GetRelativePath(Path.GetDirectoryName(installed.ManifestPath)!, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var projectManifest = manifest with
        {
            Id = "validation.project-scope",
            Scope = "project",
            Activity = null
        };
        await JsonStore.WriteAsync(Path.Combine(source, "plugin.json"), projectManifest);
        var projectArchive = Path.Combine(directory, "project.studioxplugin");
        await PluginRepository.PackAsync(source, projectArchive);
        await services.PluginManager.ImportAsync(projectArchive);
        await services.PluginManager.SetEnabledAsync(projectManifest.Id, true);
        await ReloadPluginWorkspaceAsync(CancellationToken.None);
        Check(!CanRunPlugin(projectManifest.Id) && application.Contributions.Count == 1, "工程插件在欢迎页不启动");

        foreach (var name in new[] { "project-a", "project-b" })
        {
            await StopPluginWorkspaceAsync();
            projectDirectory = Path.Combine(directory, name);
            Directory.CreateDirectory(projectDirectory);
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            Check(ReferenceEquals(application, pluginApplication) && pluginWorkspace?.Contributions.Count == 1 &&
                pluginWorkspace.Contributions.All(active => active.Manifest.Scope == "project"), name + " 只启动工程插件且保留应用会话");
            Check(ReferenceEquals(originalPanel.Host, pluginPanels[installed.Id + "/status"].Host) &&
                PluginDescendants(originalPanel.Host).OfType<TextBox>().Single().Text == "保留迁移输入", name + " 保留输入和面板实例");
        }
        var inFlight = InvokePluginCommandAsync(installed.Id, "delay", Empty());
        Check(ReferenceEquals(pluginInvocationSession, application), "在途命令绑定应用会话");
        await StopPluginWorkspaceAsync();
        projectDirectory = null;
        await inFlight;
        await Layout();
        Check(ReferenceEquals(application, pluginApplication) && Count(application, installed.Id) == 2 && pluginWorkspace is null,
            "关闭工程不取消应用级在途命令");
        Check(CanRunPlugin(installed.Id) && CapturePluginCommandContext(installed.Id).EnumerateObject().Count() == 0,
            "关闭工程后命令继续可用且不传工程上下文");

        await services.PluginManager.SetEnabledAsync(installed.Id, false);
        await Layout();
        await ReloadPluginSessionsAsync(CancellationToken.None);
        Check(!application.IsPluginRunning(installed.Id) && !pluginActivities.ContainsKey(installed.Id) &&
            !pluginPanels.ContainsKey(installed.Id + "/status") && !pluginBindingOwners.Values.Contains(installed.Id),
            "停用撤销宿主、活动入口、面板和快捷键");
        PluginWorkspace_Changed(application, new(installed.Id, "panel", JsonSerializer.SerializeToElement(
            new PluginPanelDefinition("late", "迟到面板", [new("late", "text", "不应出现")]), PluginJson)));
        FlushPluginPanels();
        Check(!pluginPanels.ContainsKey(installed.Id + "/late"), "过期应用会话迟到面板不能复活 UI");

        await services.PluginManager.SetEnabledAsync(installed.Id, true);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        Check(Count(pluginApplication!, installed.Id) == 0, "重新启用创建新的独立实例");
        var previous = pluginApplication!;
        await JsonStore.WriteAsync(Path.Combine(source, "plugin.json"), manifest with
        {
            Version = "1.0.1"
        });
        var updatedArchive = Path.Combine(directory, "updated.studioxplugin");
        await PluginRepository.PackAsync(source, updatedArchive);
        var updated = await services.PluginManager.ImportAsync(updatedArchive);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        Check(!updated.Enabled && !previous.IsPluginRunning(installed.Id) && !CanRunPlugin(installed.Id), "更新停止旧实例并重新要求信任");

        foreach (var invalid in new[]
        {
            manifest with { Scope = "unknown" },
            manifest with { HostTools = ["project_info"] },
            manifest with { Capabilities = ["commands", "panels", "agentTools"] }
        })
        {
            await JsonStore.WriteAsync(Path.Combine(source, "plugin.json"), invalid);
            try
            {
                await PluginManifest.ReadAsync(Path.Combine(source, "plugin.json"));
                throw new InvalidOperationException("范围未拒绝");
            }
            catch (StudioXException error) when (error.Code == "PLUGIN_SCOPE") { Check(true, "拒绝无效运行范围或工程能力：" + invalid.Scope + "/" + string.Join(",", invalid.HostTools ?? invalid.Capabilities)); }
        }
        try
        {
            PluginContributionValidator.Validate(manifest, new([new("context", "Context", "projectContext")], [], []));
            throw new InvalidOperationException("工程上下文未拒绝");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_CONTRIBUTION") { Check(true, "应用命令不能注册工程上下文入口"); }
        var legacyJson = JsonSerializer.SerializeToElement(projectManifest, JsonStore.Options);
        var legacy = legacyJson.EnumerateObject().Where(property => property.Name != "scope").ToDictionary(property => property.Name, property => property.Value);
        await JsonStore.WriteAsync(Path.Combine(source, "plugin.json"), legacy);
        Check((await PluginManifest.ReadAsync(Path.Combine(source, "plugin.json"))).Scope == "project", "旧清单省略 scope 默认工程范围");

        var keil = await services.PluginManager.ImportAsync(keilArchive);
        await services.PluginManager.SetEnabledAsync(keil.Id, true);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        await InvokePluginCommandAsync(keil.Id, "open", Empty());
        await Layout();
        Check(projectDirectory is null && CanRunPlugin(keil.Id) && pluginActivities.ContainsKey(keil.Id), "真实 Keil 迁移插件在欢迎页启动");
        Check(pluginApplication!.LatestPanels.Where(pair => pair.Key.StartsWith(keil.Id + "/", StringComparison.Ordinal))
            .Any(pair => pair.Value.Widgets.Any(widget => widget.Value is { ValueKind: JsonValueKind.String } value &&
                value.GetString()!.Contains("无需打开工程", StringComparison.Ordinal))), "真实迁移面板明确无需工程");
        ShowDocument(pluginActivities[keil.Id].Tab);
        Width = 1460;
        Height = 1080;
        await Layout();
        Render(this, Path.Combine(directory, "keil-welcome.png"));
        var finalSession = pluginApplication;
        await StopApplicationPluginsAsync();
        Check(!finalSession!.IsPluginRunning(keil.Id) && pluginPanels.Count == 0 && pluginActivities.Count == 0, "退出清理应用宿主及所有入口");
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            status = "passed",
            hardwareConnected = false,
            checks
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "diagnostics.txt"), PluginManager.DiagnosticLog.Text);
    }
}

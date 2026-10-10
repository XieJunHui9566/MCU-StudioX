namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    private async Task CheckPluginProjectLinksAsync(string directory, string pluginId, Action<bool, string> check, Func<Task> layout)
    {
        var project = Path.Combine(directory, "已移植 工程");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "device"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        var source = "int main(void) {\n  return invalid_value;\n}\n";
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), source);
        var sourceDocument = await services.Files.ReadAsync(project, "src/main.c");
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, "navigation-fixture", "offline.pack",
            "1.0.0", "offline", "navigation-device", "offline", "offline.tools", "1.0.0", "gcc", EntryFile: "src/main.c"));
        var device = new DeviceDefinition("navigation-device", "Offline", "arm", 0x08000000, 65536, 0x20000000, 16384,
            "offline.tools", "1.0.0", "gcc", [], [], [], [], "linker.ld", [], [], []);
        await JsonStore.WriteAsync(Path.Combine(project, "device/manifest.json"), new PackManifest(1, "offline.pack", "1.0.0", "Offline", "Offline", [device]));
        const string logPath = ".studiox/import-build.log";
        const string recordPath = ".studiox/import-build.json";
        await File.WriteAllTextAsync(Path.Combine(project, logPath), "src/main.c:2:3: error: original import diagnostic\n");
        var log = await services.Files.ReadAsync(project, logPath);
        await JsonStore.WriteAsync(Path.Combine(project, recordPath), new
        {
            formatVersion = 1,
            log = logPath,
            logSha256 = log.DiskHash,
            sourceHashes = new Dictionary<string, string> { ["src/main.c"] = sourceDocument.DiskHash }
        });
        var target = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1,
            directory = project,
            buildLog = logPath,
            buildRecord = recordPath
        });
        var panel = new PluginPanelDefinition("navigation", "打开工程", [new("project", "projectLink", "打开工程", target)]);
        PluginContributionValidator.ValidatePanel(panel, new HashSet<string>(), true);
        try
        {
            PluginContributionValidator.ValidatePanel(panel, new HashSet<string>());
            throw new InvalidOperationException("工程插件的工程入口未拒绝。");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_CONTRIBUTION")
        {
            check(true, "工程范围面板不能声明切换工程入口");
        }
        foreach (var invalid in new[]
        {
            JsonSerializer.SerializeToElement(new { formatVersion = 2, directory = project }),
            JsonSerializer.SerializeToElement(new { formatVersion = 1, directory = "relative" }),
            JsonSerializer.SerializeToElement(new { formatVersion = 1, directory = project, buildLog = "../outside.log", buildRecord = recordPath })
        })
        {
            try
            {
                await services.PluginProjectNavigation.PrepareAsync(invalid);
                throw new InvalidOperationException("无效入口未拒绝。");
            }
            catch (StudioXException) { check(true, "拒绝错误版本、相对目录或日志越界"); }
        }
        await services.PluginManager.SetEnabledAsync(pluginId, true);
        await ReloadPluginSessionsAsync(CancellationToken.None);
        var application = pluginApplication;
        await InvokePluginCommandAsync(pluginId, "link", target);
        await layout();
        var button = PluginDescendants(pluginPanels[pluginId + "/navigation"].Host).OfType<Button>().Single();
        check(projectDirectory is null, "发布工程按钮不隐式切换工作区");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        await layout();
        check(projectDirectory == project && activeEditor?.Source.RelativePath == "src/main.c", "真实声明式按钮打开工程及原源码入口");
        check(ReferenceEquals(application, pluginApplication), "打开工程保留应用级插件会话");
        check(buildDiagnostics.TryGetValue("src/main.c", out var diagnostics) && diagnostics.Items.Single().Message == "original import diagnostic",
            "迁移编译错误进入问题列表并保留原始信息");
        ProblemsGrid.SelectedItem = problemRows.Single();
        ProblemsGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        await pendingOperation;
        check(SourceEditor.Document.GetLocation(SourceEditor.CaretOffset).Line == 2, "问题列表双击定位原源码行");
        Render(this, Path.Combine(directory, "import-problems.png"));

        var invalidDirectory = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1,
            directory = Path.Combine(directory, "missing-project")
        });
        await OpenPluginProjectAsync(pluginId, invalidDirectory);
        check(projectDirectory == project && SourceEditor.Text == source, "无效工程入口保留当前工作区及编辑内容");
        var next = Path.Combine(directory, "next-project");
        Directory.CreateDirectory(Path.Combine(next, ".studiox"));
        File.Copy(Path.Combine(project, ".studiox/project.json"), Path.Combine(next, ".studiox/project.json"));
        SourceEditor.AppendText("// unsaved\n");
        await OpenPluginProjectAsync(pluginId, JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1,
            directory = next
        }), _ => MessageBoxResult.Cancel);
        check(projectDirectory == project && SourceEditor.Text.EndsWith("// unsaved\n", StringComparison.Ordinal) &&
            await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")) == source, "取消脏文档切换保留未保存内容和磁盘原文");
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), source + "// changed after compilation\n");
        await OpenPluginProjectAsync(pluginId, target);
        check(buildDiagnostics.Count == 0 && BuildLog.Text.Contains("original import diagnostic", StringComparison.Ordinal),
            "源码改变后撤销旧错误标记并保留编译原文");
        await File.AppendAllTextAsync(Path.Combine(project, logPath), "modified log\n");
        var damaged = await services.PluginProjectNavigation.PrepareAsync(target);
        check(damaged.SourceHashes.Count == 0 && damaged.Diagnostic is not null && damaged.BuildOutput!.Contains("modified log", StringComparison.Ordinal),
            "日志哈希不符时可打开工程但不发布错误标记");
        await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.No);
        await layout();
    }
}

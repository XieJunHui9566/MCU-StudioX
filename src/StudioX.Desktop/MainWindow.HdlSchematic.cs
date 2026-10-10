namespace StudioX.Desktop;

using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using StudioX.Foundation;

public partial class MainWindow
{
    private string? hdlSchematicProject;

    private void InitializeHdlSchematic()
    {
        HdlSchematic.GenerateRequested += async (_, _) => await RunAsync(GenerateHdlSchematicAsync);
        HdlSchematic.EditRequested += async (_, _) => await RunAsync(async token =>
        {
            if (currentProjectManifest?.Logic is { } logic)
            {
                await OpenSourceAsync(logic.VerilogFile, token);
            }
        });
        HdlSchematic.SourceRequested += async source => await RunAsync(token => NavigateHdlSourceAsync(source, token));
        HdlSchematic.LogRequested += async (_, _) => await RunAsync(async token =>
        {
            if (HdlSchematic.Result is { } result)
            {
                await OpenSourceAsync(Path.GetRelativePath(RequireProject(), result.LogPath).Replace('\\', '/'), token);
            }
        });
        HdlSchematic.ExportRequested += async (_, _) =>
        {
            if (HdlSchematic.Surface.Diagram is not { } diagram)
            {
                return;
            }
            var dialog = new SaveFileDialog { Filter = "SVG 电路图|*.svg", FileName = "logic-schematic.svg" };
            if (dialog.ShowDialog(this) == true)
            {
                await RunAsync(token => services.HdlSchematic.ExportSvgAsync(diagram, dialog.FileName, token));
            }
        };
        Activated += async (_, _) => await CheckHdlSchematicFreshnessAsync();
        WorkspaceTabs.SelectionChanged += async (_, _) =>
        {
            if (HdlSchematicTab.IsSelected)
            {
                await CheckHdlSchematicFreshnessAsync();
            }
        };
    }

    private void ClearHdlSchematic()
    {
        hdlWorkflowProject = null;
        HdlWorkflow.ClearResult("工程已切换，请重新运行仿真。");
        HdlWorkflowTab.Visibility = Visibility.Collapsed;
        hdlSchematicProject = null;
        HdlSchematic.Clear();
        HdlSchematicTab.Visibility = Visibility.Collapsed;
    }

    private async void OpenHdlSchematic_Click(object? sender, EventArgs e) => await RunAsync(OpenHdlSchematicAsync);

    public Task ShowHdlSchematicPageAsync() => RunAsync(OpenHdlSchematicAsync);

    private async Task OpenHdlSchematicAsync(CancellationToken token)
    {
        var root = RequireProject();
        if (hdlSchematicProject != root)
        {
            var settings = await services.HdlSchematic.ReadSettingsAsync(root, token);
            if (projectDirectory != root)
            {
                return;
            }
            HdlSchematic.Clear();
            HdlSchematic.Configure(services.HdlSchematic, settings);
            hdlSchematicProject = root;
        }
        HdlSchematicTab.Visibility = Visibility.Visible;
        ShowDocument(HdlSchematicTab);
    }

    private async Task GenerateHdlSchematicAsync(CancellationToken token)
    {
        var root = RequireProject();
        await SaveAllSourcesAsync(root, token);
        var settings = HdlSchematic.ReadSettings();
        HdlSchematic.Clear("正在综合 Verilog，可用顶部“停止”取消…");
        try
        {
            await services.HdlSchematic.SaveSettingsAsync(root, settings, token);
            var result = await RunBuildOutputOperationAsync("Verilog 综合",
                (progress, output) => services.HdlSchematic.GenerateAsync(root, settings, token, progress, output),
                _ => true, _ => "Verilog 综合成功，电路图已生成。", token);
            if (projectDirectory != root || hdlSchematicProject != root)
            {
                return;
            }
            HdlSchematic.ShowResult(result);
            Log("Verilog 电路图已生成：" + result.NetlistPath + "\n原始综合日志：" + result.LogPath);
            Status.Text = "Verilog 电路图已生成 · " + result.TopModule;
        }
        catch (Exception error)
        {
            HdlSchematic.Clear(error is OperationCanceledException ? "综合已取消。" : "综合失败，详情见构建日志。" + error.Message.Split('\n')[0]);
            throw;
        }
    }

    private async Task CheckHdlSchematicFreshnessAsync()
    {
        if (HdlSchematic.Result is not { } result || projectActionsBusy || closing)
        {
            return;
        }
        try
        {
            var dirty = editorDocuments.Any(document => document.IsDirty && result.InputHashes.ContainsKey(document.Source.RelativePath));
            var current = !dirty && await services.HdlSchematic.IsCurrentAsync(result);
            if (!current && ReferenceEquals(HdlSchematic.Result, result))
            {
                HdlSchematic.MarkStale();
            }
        }
        catch (Exception error) { Log("电路图快照检查失败：" + error); HdlSchematic.MarkStale(); }
    }

    private async Task NavigateHdlSourceAsync(string source, CancellationToken token)
    {
        // 展平后的属性可能包含多处来源；定位第一处确实属于当前工程的源文件。
        foreach (var location in source.Split('|'))
        {
            var match = Regex.Match(location, @"^(?<path>.+):(?<line>\d+)\.\d+");
            if (!match.Success)
            {
                continue;
            }
            var relative = match.Groups["path"].Value.Replace('\\', '/');
            var path = PathBoundary.Resolve(RequireProject(), relative);
            if (!File.Exists(path))
            {
                continue;
            }
            await OpenSourceAsync(relative, token);
            var line = Math.Clamp(int.Parse(match.Groups["line"].Value), 1, SourceEditor.Document.LineCount);
            SourceEditor.ScrollToLine(line);
            SourceEditor.TextArea.Caret.Line = line;
            return;
        }
        throw new StudioXException("HDL_SOURCE_LOCATION", "该综合元件没有可定位的工程源码：" + source);
    }
}

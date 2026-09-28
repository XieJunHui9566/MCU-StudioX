namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Hdl;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在软件验证工程中执行真实综合及界面交互，不建立下载或调试会话。</summary>
    public async Task RenderHdlPreviewAsync(string directory, string fixture)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task LayoutAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        await ShowAg32PinMappingAsync(CancellationToken.None);
        Check(Ag32PinMapping.SchematicButton.Visibility == Visibility.Visible, "自定义逻辑工程提供电路图入口");
        Ag32PinMapping.SchematicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(HdlSchematicTab.IsSelected && HdlSchematic.Sources.Text.Contains("user_logic.v", StringComparison.Ordinal), "入口加载工程综合配置");
        HdlSchematic.GenerateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(HdlSchematic.Result?.Modules.Length == 2 && HdlSchematic.Surface.Diagram?.Nodes.Length > 0, "按钮通过内置 Yosys 生成真实电路图");
        await LayoutAsync();
        var top = HdlSchematic.Surface.Diagram!;
        var instance = top.Nodes.Single(node => node.Type.StartsWith("$paramod", StringComparison.Ordinal));
        HdlSchematic.Surface.Select(instance);
        Check(HdlSchematic.Details.Text.Contains("WIDTH", StringComparison.Ordinal) && HdlSchematic.SourceButton.IsEnabled, "选择模块显示参数与源码入口");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await LayoutAsync();
            Render(this, Path.Combine(directory, "schematic-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        HdlSchematic.ActivateNode(instance);
        await LayoutAsync();
        Check(HdlSchematic.Surface.Diagram?.ModuleName == instance.Type && HdlSchematic.BackButton.IsEnabled, "双击子模块进入其逻辑层级");
        Render(HdlSchematic, Path.Combine(directory, "logic-unit.png"));
        var gate = HdlSchematic.Surface.Diagram!.Nodes.First(node => node.Type == "$and");
        HdlSchematic.Surface.Select(gate);
        Check(HdlSchematic.Details.Text.Contains("[8]", StringComparison.Ordinal), "元件详情显示总线位宽");
        HdlSchematic.SourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingOperation;
        Check(activeDocument?.RelativePath == "logic/logic_unit.v" && SourceEditor.TextArea.Caret.Line == 2, "元件定位到实际 Verilog 源行");
        ShowDocument(HdlSchematicTab);
        HdlSchematic.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await LayoutAsync();
        Check(HdlSchematic.Surface.Diagram?.ModuleName == "user_logic", "可返回上层模块");
        var wire = HdlSchematic.Surface.Diagram!.Wires.First(item => item.BitMappings.Length == 8);
        HdlSchematic.Surface.Select(wire);
        Check(HdlSchematic.Details.Text.Contains("[7]", StringComparison.Ordinal) && !HdlSchematic.SourceButton.IsEnabled, "连线详情保留逐位对应关系");
        Check(HdlSchematic.Surface.LayoutTransform is ScaleTransform { ScaleX: > 0 and <= 1 }, "适应窗口缩放有效");
        HdlSchematic.Defines.Text += " EXTRA=1";
        Check(HdlSchematic.Summary.Text.Contains("上次综合快照", StringComparison.Ordinal), "修改预览配置标记旧图");
        HdlSchematic.Modules.SelectedItem = instance.Type;
        Check(HdlSchematic.Summary.Text.Contains("上次综合快照", StringComparison.Ordinal), "切换模块保留失效提醒");
        RefreshAg32LogicUi(currentProjectManifest! with
        {
            Logic = null
        });
        Check(HdlSchematicTab.Visibility == Visibility.Collapsed && HdlSchematic.Result is null && Ag32PinMapping.SchematicButton.Visibility == Visibility.Collapsed,
            "基础映射模式隐藏入口并清除旧图");
        RefreshAg32LogicUi(null);
        Check(Ag32PinMappingRailButton.Visibility == Visibility.Collapsed, "无 AG32 工程时隐藏专用功能");
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            passed = checks.Count,
            checks
        });
    }
}

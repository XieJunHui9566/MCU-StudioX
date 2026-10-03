namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;

public sealed class BuildHistoryView : UserControl
{
    private readonly ComboBox baseline = new() { DisplayMemberPath = "DisplayName", MinWidth = 220, Margin = new(0, 0, 8, 8) };
    private readonly ComboBox current = new() { DisplayMemberPath = "DisplayName", MinWidth = 220, Margin = new(0, 0, 8, 8) };
    private readonly DataGrid changes = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, MinRowHeight = 30 };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    public Func<string, Task>? Requested
    {
        get; set;
    }
    public BuildHistorySnapshot? Before => baseline.SelectedItem as BuildHistorySnapshot;
    public BuildHistorySnapshot? After => current.SelectedItem as BuildHistorySnapshot;
    internal int ComparisonCount => changes.Items.Count;
    internal string DescriptionText => description.Text;
    public BuildHistoryView()
    {
        changes.SetResourceReference(StyleProperty, "DebugGrid");
        foreach (var (header, width) in new[] { ("类别", 140d), ("名称", -1d), ("基准", 85d), ("当前", 85d), ("变化", 85d) })
        {
            changes.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(header), Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new(width) });
        }
        var body = new DockPanel { Margin = new(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "构建历史与对比", FontSize = 22 });
        top.Children.Add(new TextBlock { Text = "保留本工程最近 20 次成功构建。MAP 是链接后静态占用；Ninja 列出各输出最后一次编译耗时，可能来自增量构建，不能相加当作总时间。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        var picks = new WrapPanel();
        picks.Children.Add(new TextBlock { Text = "基准", Margin = new(0, 6, 8, 0) });
        picks.Children.Add(baseline);
        picks.Children.Add(new TextBlock { Text = "比较", Margin = new(0, 6, 8, 0) });
        picks.Children.Add(current);
        top.Children.Add(picks);
        var buttons = new WrapPanel();
        foreach (var (title, action) in new[] { ("刷新历史", "refresh"), ("保存当前构建快照", "capture"), ("比较", "compare"), ("导出对比", "export") })
        {
            var button = new Button { Content = title, Margin = new(0, 0, 8, 0), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) { await run(action); } };
            buttons.Children.Add(button);
        }
        top.Children.Add(buttons);
        top.Children.Add(description);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        body.Children.Add(changes);
        Content = body;
    }
    public void SetSnapshots(IReadOnlyList<BuildHistorySnapshot> snapshots)
    {
        baseline.ItemsSource = current.ItemsSource = snapshots;
        baseline.SelectedIndex = Math.Max(0, snapshots.Count - 2);
        current.SelectedIndex = snapshots.Count - 1;
        changes.ItemsSource = null;
        description.Text = snapshots.Count < 2 ? "需要两次成功构建快照才能比较；已有构建可手动保存。" : $"已保存 {snapshots.Count} 次构建，选择两次快照比较。";
    }
    public void Compare()
    {
        if (Before is not { } before || After is not { } after)
        {
            return;
        }
        changes.ItemsSource = BuildHistoryService.Compare(before, after).Select(r => new { 类别 = r.Category, 名称 = r.Name, 基准 = r.Before, 当前 = r.After, 变化 = r.Difference.ToString("+0;-0;0") }).ToArray();
        description.Text = $"构建墙钟时间（不含健康检查/分析）：{before.BuildSeconds?.ToString("F2") ?? "未记录"} → {after.BuildSeconds?.ToString("F2") ?? "未记录"} 秒；开发环境组件版本 {before.Details.Project.ToolsetVersion} → {after.Details.Project.ToolsetVersion}。编译配置或开发环境组件版本变化也会影响结果。";
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using StudioX.Application.Editing;

/// <summary>显示任务修改的原文与结果，用户选择的块在审批之后重新校验。</summary>
internal sealed class AgentChangesView : Grid
{
    private readonly ListBox plans = new() { MinWidth = 180, Margin = new(0, 0, 12, 0) };
    private readonly Grid details = new();
    private readonly ComboBox files = new() { DisplayMemberPath = "Path", Margin = new(0, 0, 0, 8) };
    private readonly ListBox hunks = new() { MaxHeight = 110, Margin = new(0, 0, 0, 8) };
    internal CodeComparisonView Comparison { get; } = new();
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    private readonly Dictionary<string, HashSet<string>> selected = new();
    private IReadOnlyList<AgentEditPlan> currentPlans = [];
    private AgentEditPlan? shownPlan;
    private string? selectedPath;
    private int currentHunk;
    private bool preservingView;
    private Button? applyButton;
    internal event Action<string, Exception>? HighlightingFailed;
    public Func<string, Task>? ApplyRequested
    {
        get; set;
    }
    public Func<string, Task>? UndoRequested
    {
        get; set;
    }
    public Func<string, Task>? UndoTaskRequested
    {
        get; set;
    }
    public Func<bool>? CanAct
    {
        get; set;
    }

    public AgentChangesView()
    {
        Margin = new(14);
        RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new()
        {
            Height = new(0)
        });
        ColumnDefinitions.Add(new()
        {
            Width = new(190)
        });
        ColumnDefinitions.Add(new());
        SetColumnSpan(heading, 2);
        Children.Add(heading);
        SetRow(plans, 1);
        Children.Add(plans);
        for (var row = 0; row < 4; row++)
        {
            details.RowDefinitions.Add(new()
            {
                Height = GridLength.Auto
            });
        }
        details.RowDefinitions.Add(new());
        SetRow(Comparison, 4);
        Comparison.Visibility = Visibility.Collapsed;
        details.Children.Add(Comparison);
        SetRow(details, 1);
        SetColumn(details, 1);
        Children.Add(details);
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 900;
            ColumnDefinitions[0].Width = compact ? new(1, GridUnitType.Star) : new(190);
            ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
            RowDefinitions[1].Height = compact ? GridLength.Auto : new(1, GridUnitType.Star);
            RowDefinitions[2].Height = compact ? new(1, GridUnitType.Star) : new(0);
            plans.Height = compact ? Math.Min(74, Math.Max(32, plans.Items.Count * 32)) : double.NaN;
            plans.Margin = compact ? new(0, 0, 0, 4) : new(0, 0, 12, 0);
            SetColumnSpan(plans, compact ? 2 : 1);
            SetRow(details, compact ? 2 : 1);
            SetColumn(details, compact ? 0 : 1);
            SetColumnSpan(details, compact ? 2 : 1);
        };
        plans.SetResourceReference(BackgroundProperty, "ToolSurface");
        plans.SetResourceReference(Control.ForegroundProperty, "Text");
        plans.SetResourceReference(Control.BorderBrushProperty, "Border");
        plans.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "CodeCompletionItemStyle");
        ScrollViewer.SetHorizontalScrollBarVisibility(plans, ScrollBarVisibility.Disabled);
        hunks.SetResourceReference(BackgroundProperty, "EditorSurface");
        hunks.SetResourceReference(Control.ForegroundProperty, "Text");
        hunks.SetResourceReference(Control.BorderBrushProperty, "Border");
        hunks.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "CodeCompletionItemStyle");
        ScrollViewer.SetHorizontalScrollBarVisibility(hunks, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(plans, "AI 修改计划");
        System.Windows.Automation.AutomationProperties.SetName(files, "审阅的文件");
        System.Windows.Automation.AutomationProperties.SetName(hunks, "选择修改块并定位");
        plans.SelectionChanged += (_, _) => ShowSelected();
        files.SelectionChanged += (_, _) => ShowFile();
        Comparison.HighlightingFailed += (language, exception) => HighlightingFailed?.Invoke(language, exception);
    }
    internal void BindTypography(TextEditor source) => Comparison.BindTypography(source);
    internal void RefreshTheme() => Comparison.RefreshTheme();
    public void Refresh(AgentEditorSession current, AgentEditPlan? focus = null)
        => Refresh(current.TaskTitle, current.ValidationStatus, current.Plans, focus);

    internal void Refresh(string taskTitle, string validationStatus, IReadOnlyList<AgentEditPlan> current, AgentEditPlan? focus = null)
    {
        var previous = (plans.SelectedItem as ListBoxItem)?.Tag as AgentEditPlan;
        var wasPreserving = preservingView;
        preservingView = previous is not null && current.Any(plan => plan.Id == previous.Id) && (focus is null || focus.Id == previous.Id);
        try
        {
            RefreshPlans(taskTitle, validationStatus, current, focus, previous);
        }
        finally { preservingView = wasPreserving; }
    }

    private void RefreshPlans(string taskTitle, string validationStatus, IReadOnlyList<AgentEditPlan> current, AgentEditPlan? focus, AgentEditPlan? previous)
    {
        currentPlans = current;
        heading.Text = taskTitle + "\n" + validationStatus + " · 修改在缓冲区，保存后才写入源码。";
        plans.Items.Clear();
        foreach (var plan in current)
        {
            EnsureSelection(plan);
            var title = plan.Status switch
            {
                "pending" => "待应用",
                "applied" => "已应用",
                "reverted" => "已撤销",
                _ => "已拒绝"
            };
            var item = new ListBoxItem
            {
                Content = new TextBlock { Text = title + " · " + plan.Reason, TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = plan
            };
            plans.Items.Add(item);
            if (plan.Id == (focus ?? previous)?.Id)
            {
                plans.SelectedItem = item;
            }
        }
        if (plans.SelectedItem is null && plans.Items.Count > 0)
        {
            plans.SelectedIndex = plans.Items.Count - 1;
        }
        ShowSelected();
    }
    public IReadOnlyList<WorkspaceFileChange> Selected(AgentEditPlan plan)
    {
        EnsureSelection(plan);
        return plan.Changes.Select(c =>
        {
            var matches = c.Matches.Where((_, i) => selected[plan.Id].Contains(Key(c.Path, i))).ToArray();
            return c with
            {
                Matches = matches,
                After = WorkspaceEditService.ApplyText(c.Before, matches)
            };
        }).Where(c => c.CanApply).ToArray();
    }
    private void EnsureSelection(AgentEditPlan plan)
    {
        if (plan.Status != "pending" || !selected.ContainsKey(plan.Id))
        {
            selected[plan.Id] = plan.Changes.SelectMany(c => c.Matches.Select((_, i) => Key(c.Path, i))).ToHashSet();
        }
    }
    private static string Key(string path, int index) => path + ":" + index;
    private void ShowSelected()
    {
        // 审阅编辑器保持挂载；刷新状态时卸载滚动容器会清空其视口。
        foreach (var child in details.Children.Cast<UIElement>().Where(child => child != Comparison).ToArray())
        {
            details.Children.Remove(child);
        }
        shownPlan = null;
        if ((plans.SelectedItem as ListBoxItem)?.Tag is not AgentEditPlan plan)
        {
            if (!preservingView)
            {
                Comparison.Visibility = Visibility.Collapsed;
            }
            return;
        }
        Comparison.Visibility = Visibility.Visible;
        shownPlan = plan;
        var actions = new WrapPanel();
        var action = new Button
        {
            Content = plan.Status == "pending" ? "应用所选修改" : "撤销此计划",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = plan.Status is "pending" or "applied" && (CanAct?.Invoke() ?? true),
            Margin = new(0, 0, 0, 8)
        };
        applyButton = action;
        if (plan.Status == "pending")
        {
            action.SetResourceReference(StyleProperty, "PrimaryButton");
        }
        action.Click += async (_, _) => { if (plan.Status == "pending" && ApplyRequested is { } apply) { await apply(plan.Id); } else if (UndoRequested is { } undo) { await undo(plan.Id); } };
        actions.Children.Add(action);
        details.Children.Add(actions);
        if (currentPlans.Any(p => p.TaskId == plan.TaskId && p.Status == "applied"))
        {
            var undoTask = new Button
            {
                Content = "撤销整个任务的修改",
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = CanAct?.Invoke() ?? true,
                Margin = new(8, 0, 0, 8)
            };
            undoTask.Click += async (_, _) => { if (UndoTaskRequested is { } undo) { await undo(plan.TaskId); } };
            actions.Children.Add(undoTask);
        }
        var hint = new TextBlock
        {
            Text = plan.Status == "pending" ? plan.IsAtomic ? "语义重命名整体应用；点击修改块定位。" : "勾选要保留的块，点击定位；对照同步更新。" :
                "对照为此计划的原始快照与修改结果；后续手工编辑仍按冲突检查保护。",
            ToolTip = "Agent 等待授权时，请在聊天区允许。语义重命名须整体应用。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 8)
        };
        SetRow(hint, 1);
        details.Children.Add(hint);
        var picker = new DockPanel();
        if (files.Parent is Panel previousPicker)
        {
            previousPicker.Children.Remove(files);
        }
        picker.Children.Add(new TextBlock { Text = "文件", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 8) });
        picker.Children.Add(files);
        SetRow(picker, 2);
        details.Children.Add(picker);
        SetRow(hunks, 3);
        details.Children.Add(hunks);
        files.ItemsSource = plan.Changes;
        files.SelectedItem = plan.Changes.FirstOrDefault(c => c.Path == selectedPath) ?? plan.Changes.FirstOrDefault();
        ShowFile();
    }

    private void ShowFile()
    {
        hunks.Items.Clear();
        if (shownPlan is not { } plan || files.SelectedItem is not WorkspaceFileChange change)
        {
            return;
        }
        currentHunk = preservingView && selectedPath == change.Path ? Math.Clamp(currentHunk, 0, Math.Max(0, change.Matches.Count - 1)) : 0;
        selectedPath = change.Path;
        ShowComparison();
        for (var i = 0; i < change.Matches.Count; i++)
        {
            var index = i;
            var match = change.Matches[i];
            var key = Key(change.Path, i);
            var row = new DockPanel { LastChildFill = true };
            var check = new CheckBox
            {
                IsChecked = selected[plan.Id].Contains(key),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = plan.Status == "pending" && !plan.IsAtomic,
                Margin = new(4, 0, 6, 0)
            };
            System.Windows.Automation.AutomationProperties.SetName(check, $"保留修改 {i + 1}");
            check.Checked += (_, _) => { selected[plan.Id].Add(key); currentHunk = index; ShowComparison(); };
            check.Unchecked += (_, _) => { selected[plan.Id].Remove(key); currentHunk = index; ShowComparison(); };
            row.Children.Add(check);
            var navigate = new Button
            {
                Content = $"修改 {i + 1} · 第 {Comparison.Before.Document.GetLineByOffset(match.Start).LineNumber} 行 · 定位",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(5, 2, 5, 2)
            };
            navigate.Click += (_, _) => { currentHunk = index; FocusHunk(); };
            row.Children.Add(navigate);
            hunks.Items.Add(row);
        }
    }

    private void ShowComparison()
    {
        if (shownPlan is not { } plan || files.SelectedItem is not WorkspaceFileChange change)
        {
            return;
        }
        var matches = change.Matches.Where((_, i) => selected[plan.Id].Contains(Key(change.Path, i))).ToArray();
        var changed = Comparison.Show(change.Path, change.Before, WorkspaceEditService.ApplyText(change.Before, matches));
        if (applyButton is { } button && plan.Status == "pending")
        {
            button.IsEnabled = selected[plan.Id].Count > 0 && (CanAct?.Invoke() ?? true);
        }
        if (changed || !preservingView)
        {
            FocusHunk();
        }
    }

    private void FocusHunk()
    {
        if (files.SelectedItem is not WorkspaceFileChange change || shownPlan is not { } plan || change.Matches.Count == 0)
        {
            return;
        }
        var match = change.Matches[Math.Min(currentHunk, change.Matches.Count - 1)];
        var delta = change.Matches.Where((m, i) => m.Start < match.Start && selected[plan.Id].Contains(Key(change.Path, i)))
            .Sum(m => m.Replacement.Length - m.Length);
        Comparison.FocusChange(match.Start, match.Start + delta);
    }
}

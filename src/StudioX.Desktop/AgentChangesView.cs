namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Editing;

/// <summary>显示任务修改的原文与结果，用户选择的块在审批之后重新校验。</summary>
internal sealed class AgentChangesView : Grid
{
    private readonly ListBox plans = new() { MinWidth = 180, Margin = new(0, 0, 12, 0) };
    private readonly StackPanel details = new();
    private readonly ScrollViewer detailScroll;
    private readonly TextBlock heading = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
    private readonly Dictionary<string, HashSet<string>> selected = new();
    private AgentEditorSession? session;
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
        detailScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(detailScroll, 1);
        SetColumn(detailScroll, 1);
        Children.Add(detailScroll);
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 600;
            ColumnDefinitions[0].Width = compact ? new(1, GridUnitType.Star) : new(190);
            ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
            RowDefinitions[1].Height = compact ? GridLength.Auto : new(1, GridUnitType.Star);
            RowDefinitions[2].Height = compact ? new(1, GridUnitType.Star) : new(0);
            plans.Height = compact ? 74 : double.NaN;
            plans.Margin = compact ? new(0, 0, 0, 10) : new(0, 0, 12, 0);
            SetColumnSpan(plans, compact ? 2 : 1);
            SetRow(detailScroll, compact ? 2 : 1);
            SetColumn(detailScroll, compact ? 0 : 1);
            SetColumnSpan(detailScroll, compact ? 2 : 1);
        };
        plans.SetResourceReference(BackgroundProperty, "ToolSurface");
        plans.SetResourceReference(Control.ForegroundProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(plans, "AI 修改计划");
        plans.SelectionChanged += (_, _) => ShowSelected();
    }
    public void Refresh(AgentEditorSession current, AgentEditPlan? focus = null)
    {
        session = current;
        var previous = (plans.SelectedItem as ListBoxItem)?.Tag as AgentEditPlan;
        heading.Text = current.TaskTitle + "\n" + current.ValidationStatus + " · 修改在缓冲区，保存后才写入源码。";
        plans.Items.Clear();
        foreach (var plan in current.Plans)
        {
            EnsureSelection(plan);
            var title = plan.Status switch
            {
                "pending" => "待应用",
                "applied" => "已应用",
                "reverted" => "已撤销",
                _ => "已拒绝"
            };
            var item = new ListBoxItem { Content = new TextBlock { Text = title + " · " + plan.Reason, TextWrapping = TextWrapping.Wrap }, Tag = plan };
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
        details.Children.Clear();
        if ((plans.SelectedItem as ListBoxItem)?.Tag is not AgentEditPlan plan)
        {
            return;
        }
        var action = new Button
        {
            Content = plan.Status == "pending" ? "应用所选修改" : "撤销此计划",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = plan.Status is "pending" or "applied" && (CanAct?.Invoke() ?? true),
            Margin = new(0, 0, 0, 8)
        };
        action.Click += async (_, _) => { if (plan.Status == "pending" && ApplyRequested is { } apply) { await apply(plan.Id); } else if (UndoRequested is { } undo) { await undo(plan.Id); } };
        details.Children.Add(action);
        if (session?.Plans.Any(p => p.TaskId == plan.TaskId && p.Status == "applied") == true)
        {
            var undoTask = new Button
            {
                Content = "撤销整个任务的修改",
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = CanAct?.Invoke() ?? true,
                Margin = new(0, 0, 0, 8)
            };
            undoTask.Click += async (_, _) => { if (UndoTaskRequested is { } undo) { await undo(plan.TaskId); } };
            details.Children.Add(undoTask);
        }
        if (plan.Status == "pending")
        {
            details.Children.Add(new TextBlock { Text = "勾选要保留的块；Agent 正等待授权时，审阅后点击聊天区的允许。语义重命名整体应用。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        }
        foreach (var change in plan.Changes)
        {
            details.Children.Add(new TextBlock { Text = change.Path, FontWeight = FontWeights.SemiBold, Margin = new(0, 12, 0, 6) });
            for (var i = 0; i < change.Matches.Count; i++)
            {
                var match = change.Matches[i];
                var key = Key(change.Path, i);
                var check = new CheckBox
                {
                    Content = $"修改 {i + 1} · 第 {change.Before[..match.Start].Count(c => c == '\n') + 1} 行",
                    IsChecked = selected[plan.Id].Contains(key),
                    IsEnabled = plan.Status == "pending" && !plan.IsAtomic
                };
                check.Checked += (_, _) => selected[plan.Id].Add(key);
                check.Unchecked += (_, _) => selected[plan.Id].Remove(key);
                details.Children.Add(check);
                AddText("修改前", change.Before.Substring(match.Start, match.Length));
                AddText("修改后", match.Replacement);
            }
        }
    }
    private void AddText(string label, string text)
    {
        details.Children.Add(new TextBlock { Text = label, Margin = new(0, 5, 0, 3) });
        var box = new TextBox
        {
            Text = text.Length > 16000 ? text[..16000] + "\n[预览截断]" : text,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new("Consolas"),
            MaxHeight = 170,
            MinHeight = 32,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        box.SetResourceReference(Control.BackgroundProperty, "EditorSurface");
        box.SetResourceReference(Control.ForegroundProperty, "Text");
        details.Children.Add(box);
    }
}

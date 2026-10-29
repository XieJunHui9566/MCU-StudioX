namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Skills;

/// <summary>聊天区的技能导入和选择入口；完整正文仍由 Agent 按需读取。</summary>
internal sealed class AiSkillsWindow : Window
{
    private readonly AiSkillService service;
    private readonly string project;
    private readonly ListBox skills = new() { MinHeight = 170, DisplayMemberPath = nameof(AgentSkillMetadata.Name) };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly Button importFile = new() { Content = "导入 SKILL.md", Margin = new(0, 0, 8, 0) };
    private readonly Button importFolder = new() { Content = "导入文件夹" };
    public string? SelectedSkill
    {
        get; private set;
    }

    public AiSkillsWindow(AiSkillService service, string project)
    {
        this.service = service;
        this.project = project;
        Title = "AI 技能";
        Width = 540;
        Height = 470;
        MinWidth = 440;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        skills.SetResourceReference(Control.BackgroundProperty, "EditorSurface");
        skills.SetResourceReference(Control.ForegroundProperty, "Text");
        skills.SetResourceReference(Control.BorderBrushProperty, "Border");
        var panel = new DockPanel { Margin = new(22) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "技能", FontSize = 22, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "内置代码风格技能默认可用；导入后 AI 按任务需要读取技能。\n只导入可信来源，技能不会授予工具权限或自动执行脚本。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 10, 0, 14)
        });
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var footer = new StackPanel();
        footer.Children.Add(details);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(importFile);
        actions.Children.Add(importFolder);
        var use = new Button { Content = "使用所选技能", Margin = new(12, 0, 0, 0) };
        actions.Children.Add(use);
        footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(skills);
        Content = panel;
        skills.SelectionChanged += (_, _) =>
        {
            if (skills.SelectedItem is AgentSkillMetadata skill)
            {
                details.Text = $"{skill.Scope} · {skill.Description}";
            }
        };
        importFile.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Agent Skill (SKILL.md)|SKILL.md", CheckFileExists = true };
            if (picker.ShowDialog(this) == true)
            {
                await ImportAsync(picker.FileName);
            }
        };
        importFolder.Click += async (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择包含 SKILL.md 的技能文件夹" };
            if (picker.ShowDialog(this) == true)
            {
                await ImportAsync(picker.FolderName);
            }
        };
        use.Click += (_, _) =>
        {
            if (skills.SelectedItem is AgentSkillMetadata skill)
            {
                SelectedSkill = skill.Name;
                DialogResult = true;
            }
        };
        Refresh();
    }

    private void Refresh(string? selected = null)
    {
        var discovery = service.Discover(project);
        skills.ItemsSource = discovery.Skills;
        skills.SelectedItem = discovery.Skills.FirstOrDefault(skill => skill.Name == (selected ?? "mcu-code-style")) ?? discovery.Skills.FirstOrDefault();
        if (discovery.Diagnostics.Count > 0)
        {
            details.Text = string.Join("\n", discovery.Diagnostics);
        }
    }

    private async Task ImportAsync(string path)
    {
        importFile.IsEnabled = importFolder.IsEnabled = false;
        try
        {
            var imported = await service.ImportAsync(path);
            Refresh(imported.Name);
            details.Text = $"已导入 {imported.Name}，可直接使用。";
        }
        catch (Exception ex) { details.Text = ex.Message; }
        finally { importFile.IsEnabled = importFolder.IsEnabled = true; }
    }
}

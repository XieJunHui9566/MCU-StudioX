namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Skills;

public partial class MainWindow
{
    private int skillsRefreshGeneration;
    private bool skillsToggleUpdating;

    private void RefreshSkillsForProjectChange()
    {
        ++skillsRefreshGeneration;
        if (ExtensionsTab.IsSelected)
        {
            _ = RefreshSkillsAsync();
            return;
        }
        SkillsList.ItemsSource = null;
        SkillsProjectText.Text = projectDirectory is null
            ? "打开工程后查看可用技能。" : "当前工程：" + projectDirectory;
        SkillsEmptyHint.Text = "打开技能页后读取可用技能。";
        SkillsEmptyHint.Visibility = Visibility.Visible;
        SkillsDiagnosticText.Visibility = Visibility.Collapsed;
        SkillsRefreshButton.IsEnabled = projectDirectory is not null;
        ProjectSkillsEnabledToggle.IsEnabled = false;
        SetProjectSkillsToggle(false);
    }

    private async void WorkspaceTabs_SkillsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs) ||
            !ReferenceEquals(WorkspaceTabs.SelectedItem, ExtensionsTab) || SkillsList is null) return;
        await RefreshSkillsAsync();
    }

    private async void SkillsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshSkillsAsync();

    private async void ProjectSkillsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (skillsToggleUpdating || projectDirectory is not { } project) return;
        var enable = ProjectSkillsEnabledToggle.IsChecked == true;
        if (enable)
        {
            var decision = MessageBox.Show(this,
                $"允许 AI 助手读取并遵循此工程的 .agents/skills 技能说明吗？\n\n{project}\n\n" +
                "工程技能可能来自不可信仓库。启用只对本工程生效；文件、Git 和设备操作仍需单独确认。",
                "启用工程技能", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (decision != MessageBoxResult.Yes)
            {
                SetProjectSkillsToggle(false);
                return;
            }
        }

        try
        {
            // 项目技能信任由应用服务保存；取消勾选时同步撤销，随后重新发现可用技能。
            services.AiSkills.SetProjectEnabled(project, enable);
            Status.Text = enable ? "已启用此工程的 AI 技能。" : "已禁用此工程的 AI 技能。";
            await RefreshSkillsAsync();
        }
        catch (Exception error)
        {
            SetProjectSkillsToggle(!enable);
            SkillsDiagnosticText.Text = "无法更改工程技能设置：" + error.Message;
            SkillsDiagnosticText.Visibility = Visibility.Visible;
            Log("工程技能设置失败：" + error);
        }
    }

    private async Task RefreshSkillsAsync()
    {
        var generation = ++skillsRefreshGeneration;
        var project = projectDirectory;
        SkillsProjectText.Text = project is null ? "打开工程后查看可用技能。" : "当前工程：" + project;
        SkillsRefreshButton.IsEnabled = project is not null;
        ProjectSkillsEnabledToggle.IsEnabled = false;
        SkillsList.ItemsSource = null;
        SkillsEmptyHint.Text = project is null ? "打开工程后查看可用技能。" : "正在读取技能…";
        SkillsEmptyHint.Visibility = Visibility.Visible;
        SkillsDiagnosticText.Visibility = Visibility.Collapsed;
        if (project is null)
        {
            SetProjectSkillsToggle(false);
            return;
        }

        try
        {
            var enabled = services.AiSkills.IsProjectEnabled(project);
            SetProjectSkillsToggle(enabled);
            SkillsRefreshButton.IsEnabled = false;
            var discovery = await Task.Run(() => services.AiSkills.Discover(project));
            if (generation != skillsRefreshGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;

            SkillsList.ItemsSource = discovery.Skills.Select(SkillDisplayRow.From).ToArray();
            SkillsEmptyHint.Text = "尚未发现可用技能。";
            SkillsEmptyHint.Visibility = discovery.Skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (discovery.Diagnostics.Count > 0)
            {
                SkillsDiagnosticText.Text = string.Join(Environment.NewLine, discovery.Diagnostics);
                SkillsDiagnosticText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception error)
        {
            if (generation != skillsRefreshGeneration) return;
            SkillsEmptyHint.Text = "无法读取技能。";
            SkillsDiagnosticText.Text = error.Message;
            SkillsDiagnosticText.Visibility = Visibility.Visible;
            Log("AI 技能发现失败：" + error);
        }
        finally
        {
            if (generation == skillsRefreshGeneration &&
                string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                SkillsRefreshButton.IsEnabled = true;
                ProjectSkillsEnabledToggle.IsEnabled = true;
            }
        }
    }

    private void SetProjectSkillsToggle(bool enabled)
    {
        skillsToggleUpdating = true;
        try { ProjectSkillsEnabledToggle.IsChecked = enabled; }
        finally { skillsToggleUpdating = false; }
    }

    private sealed record SkillDisplayRow(string Name, string Description, string ScopeLabel)
    {
        public static SkillDisplayRow From(AgentSkillMetadata skill) => new(
            skill.Name, skill.Description, skill.Scope switch
            {
                "project" => "当前工程",
                "user" => "用户目录",
                "bundled" => "内置技能",
                _ => skill.Scope
            });
    }
}

namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Onboarding;
using StudioX.Engine;

public partial class MainWindow
{
    private TabItem? firstProjectTab;
    private FirstProjectGuideView? firstProjectView;
    private bool guideSaved;
    private bool guideHealthPassed;
    private BuildReport? guideBuild;

    private FirstProjectProgress GuideProgress() => new(projectDirectory, currentProjectManifest?.Name ?? "",
        currentProjectManifest?.Zephyr?.BoardTarget ?? currentProjectManifest?.DeviceId ?? "",
        currentProjectManifest is { } project ? project.ToolsetId + " " + project.ToolsetVersion : "",
        IsMicroPythonProject, IsZephyrProject, IsEspressifProject, guideHealthPassed, guideSaved, guideBuild?.Success, guideBuild?.Artifacts);

    private Task ShowFirstProjectAsync()
    {
        if (firstProjectTab is null || !WorkspaceTabs.Items.Contains(firstProjectTab))
        {
            firstProjectView = new FirstProjectGuideView
            {
                ActionRequested = RunGuideActionAsync, HelpRequested = id => ShowHelpAsync(id),
                Failed = error => Log(error.ToString()),
                FinishRequested = async () =>
                {
                    await services.FirstProjectGuide.DismissAsync();
                    await CloseWorkspaceTabAsync(firstProjectTab!);
                    ShowDocument(WelcomeTab);
                    Status.Text = "引导已结束；可从欢迎页或帮助菜单重新打开。";
                }
            };
            firstProjectTab = AddToolTab("第一个工程引导", firstProjectView);
        }
        firstProjectView!.Refresh(GuideProgress());
        ShowDocument(firstProjectTab);
        return Task.CompletedTask;
    }
    private void RefreshFirstProjectGuide()
    {
        // 教程不可在每次击键时重建列表；不可见时只收集证据，重新选中标签时刷新。
        if (firstProjectView is not null && ReferenceEquals(WorkspaceTabs.SelectedItem, firstProjectTab))
            firstProjectView.Refresh(GuideProgress());
    }
    private void ResetFirstProjectEvidence() { guideSaved = guideHealthPassed = false; guideBuild = null; RefreshFirstProjectGuide(); }
    private async void FirstProject_Click(object sender, RoutedEventArgs e) => await ShowFirstProjectAsync();
    public Task OpenFirstProjectGuideAsync() => ShowFirstProjectAsync();

    public async Task OfferFirstProjectGuideAsync()
    {
        try
        {
            if (projectDirectory is null && (await services.RecentProjects.LoadAsync()).Count == 0 && !await services.FirstProjectGuide.IsDismissedAsync())
                await ShowFirstProjectAsync();
        }
        catch (Exception error) { Log("首次工程引导偏好读取失败：" + error); }
    }

    private async Task RunGuideActionAsync(string action)
    {
        switch (action)
        {
            case "tools": await ShowToolEnvironmentAsync(); break;
            case "create": await RunAsync(token => BeginNewProjectAsync(token)); break;
            case "open": OpenProject_Click(this, new RoutedEventArgs()); break;
            case "health":
                healthDirectory = projectDirectory;
                await ShowProjectHealthAsync();
                break;
            case "source":
                await RunAsync(async token =>
                {
                    var project = currentProjectManifest ?? throw new InvalidOperationException("请先打开工程。");
                    var entry = project.EntryFile ?? (project.Kind == ProjectKind.CubeMx ? "Core/Src/main.c" : project.Kind == ProjectKind.MicroPython ? "main.py" : "src/main.c");
                    await OpenSourceAsync(entry, token);
                });
                break;
            case "save":
                await RunAsync(async token =>
                {
                    await SaveAllSourcesAsync(RequireProject(), token);
                    guideSaved = editorDocuments.Count > 0 && editorDocuments.All(editor => !editor.IsDirty);
                    RefreshFirstProjectGuide();
                    Status.Text = "已保存全部打开的文件。";
                });
                break;
            case "build": Build_Click(this, new RoutedEventArgs()); break;
            case "output": ShowBottom(0); break;
            case "help": await ShowHelpAsync(FirstProjectGuideService.Steps(GuideProgress())[firstProjectView!.SelectedStep].HelpTopic); break;
        }
        RefreshFirstProjectGuide();
    }
}

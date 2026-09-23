namespace StudioX.Desktop;

using System.IO;
using System.Windows;
using StudioX.Engine;

public partial class MainWindow
{
    private async void GitGraph_Click(object sender, RoutedEventArgs e)
    {
        ShowDocument(GitGraphTab);
        await GitGraph.EnsureLoadedAsync();
    }

    private async Task<bool> PrepareGitStageAsync(CancellationToken token)
    {
        if (GitGraph.RepositoryDirectory is not { } directory) return false;
        if (!GitGraphAffectsOpenProject(directory)) return true;
        if (!editorDocuments.Any(session => session.IsDirty)) return true;
        var choice = MessageBox.Show(this,
            "有尚未保存的代码文件。暂存前是否保存全部文件？",
            "暂存文件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return false;
        await SaveAllSourcesAsync(projectDirectory!, token);
        return true;
    }

    private async Task<bool> PrepareGitWorkingTreeChangeAsync(CancellationToken token)
    {
        var directory = GitGraph.RepositoryDirectory;
        if (directory is null) return false;
        if (!GitGraphAffectsOpenProject(directory)) return true;
        if (services.Debugger.IsActive)
        {
            MessageBox.Show(this, "请先结束调试，再切换分支、合并或拉取。", "Git 工作区操作", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!editorDocuments.Any(session => session.IsDirty)) return true;
        var choice = MessageBox.Show(this,
            "有尚未保存的代码文件。切换分支、合并或拉取前，是否保存全部文件？",
            "保存代码文件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return false;
        await SaveAllSourcesAsync(projectDirectory!, token);
        return true;
    }

    private async Task ApplyGitWorkingTreeChangeAsync(CancellationToken token)
    {
        if (projectDirectory is not { } directory) return;
        if (GitGraph.RepositoryDirectory is not { } repository || !GitGraphAffectsOpenProject(repository)) return;
        bool IsCurrentProject() => !token.IsCancellationRequested &&
            string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase);
        // Git 可能改写代码与工程清单；丢弃已保存标签的旧快照，防止旧文本覆盖新分支。
        ClearEditorDocuments();
        BuildMemory.SetMessage("Git 更新了工作区；重新编译后显示当前分支的占用。");
        var manifestReady = true;
        try
        {
            var project = await ProjectService.ReadAsync(directory, token);
            if (!IsCurrentProject()) return;
            SetProjectDetailsMode(project);
            WindowProjectTitle.Text = project.Name;
            Title = project.Name + " — MCU StudioX";
            PopulateProjectTree(project.Name);
            BuildConfiguration.Text = project.Name + " · " + (project.CubeMx?.ConfigurePreset ?? project.CubeMx?.BuildType ?? "Debug");
            DeviceLabel.Text = "器件 / " + project.DeviceId;
            ToolsetLabel.Text = $"工具集 / {project.ToolsetId} {project.ToolsetVersion}";
            await services.Debugger.OpenProjectAsync(directory, token);
            if (!IsCurrentProject()) return;
            var downloadConfiguration = await services.Downloads.ConfigurationAsync(directory, token);
            if (!IsCurrentProject()) return;
            ApplyDownloadConfiguration(downloadConfiguration);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!IsCurrentProject()) return;
            manifestReady = false;
            PopulateProjectTree(WindowProjectTitle.Text);
            Log("Git 更新后工程清单读取失败：" + ex);
        }
        try
        {
            if (!IsCurrentProject()) return;
            await services.Intelligence.StopAsync();
            if (!IsCurrentProject()) return;
            await services.Intelligence.StartAsync(directory, token);
            if (!IsCurrentProject()) return;
            QueueOutlineRefresh(clear: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log("Git 工作区更新后代码索引刷新失败：" + ex); }
        if (!IsCurrentProject()) return;
        Status.Text = manifestReady
            ? "Git 工作区已更新；代码标签已关闭，请按需重新打开。"
            : "Git 工作区已更新，但当前分支的工程清单无法读取；请查看构建日志并切回可用分支。";
    }

    private bool GitGraphAffectsOpenProject(string repository)
    {
        if (projectDirectory is null) return false;
        var relative = Path.GetRelativePath(repository, projectDirectory);
        return relative == "." || relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}

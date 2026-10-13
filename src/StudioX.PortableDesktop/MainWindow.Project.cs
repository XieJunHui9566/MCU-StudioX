namespace StudioX.PortableDesktop;

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StudioX.Application;

internal sealed partial class MainWindow
{
    private async Task PickProjectAsync(CancellationToken token)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择已有 StudioX 工程目录",
            AllowMultiple = false
        });
        if (selected.Count == 1 && selected[0].TryGetLocalPath() is { } path)
        {
            if (await ConfirmBuffersAsync(buffers, token))
            {
                await OpenProjectAsync(path, token);
            }
        }
    }

    private async Task OpenProjectAsync(string directory, CancellationToken token)
    {
        var project = await workspace.OpenAsync(directory, token);
        ClearDocuments();
        tree.ItemsSource = await CreateNodesAsync("", token);
        status.Text = "已打开工程：" + project.Directory;
        try
        {
            await LoadDocumentAsync(project.EntryFile, token);
        }
        catch (FileNotFoundException error)
        {
            // 工程入口可以被用户重构；工程树保持可用，并展示原始缺失诊断。
            AppendLog(error.ToString());
            status.Text = "入口文件不存在，请从工程树选择源文件";
        }
        UpdateControls();
    }

    private async Task<IReadOnlyList<TreeViewItem>> CreateNodesAsync(string relative, CancellationToken token)
    {
        var entries = await workspace.ListAsync(relative, token);
        return entries.Where(entry => entry.Name != ".build" && entry.Name != ".studiox")
            .Select(CreateNode).ToArray();
    }

    private TreeViewItem CreateNode(ProjectEntry entry)
    {
        var item = new TreeViewItem
        {
            Header = new Avalonia.Controls.StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 7,
                Children = { FileIcon(entry.IsDirectory ? "folder" : entry.RelativePath), new TextBlock { Text = entry.Name + (entry.IsLink ? " · 链接" : "") } }
            },
            Tag = entry
        };
        if (entry.IsDirectory && !entry.IsLink)
        {
            var loaded = false;
            item.ItemsSource = new[] { new TreeViewItem { Header = "加载中…" } };
            item.PropertyChanged += (_, change) =>
            {
                if (change.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !loaded)
                {
                    RunOperationAsync(async token =>
                    {
                        item.ItemsSource = await CreateNodesAsync(entry.RelativePath, token);
                        loaded = true;
                    });
                }
            };
        }
        return item;
    }

    private async Task OpenSelectedFileAsync(CancellationToken token)
    {
        if (tree.SelectedItem is TreeViewItem { Tag: ProjectEntry { IsDirectory: false, IsLink: false } entry })
        {
            await LoadDocumentAsync(entry.RelativePath, token);
        }
    }

    private async Task BuildProjectAsync(CancellationToken token)
    {
        if (workspace.Project is null)
        {
            return;
        }
        await SaveAllAsync(token);
        ShowOutput(true);
        log.Text = "";
        status.Text = "准备构建…";
        var progress = new Progress<string>(text => status.Text = text);
        var output = new Progress<string>(AppendLog);
        var result = await workspace.BuildAsync(progress, output, token);
        status.Text = result.Summary;
        AppendLog(result.Summary);
        if (result.LogPath is not null)
        {
            AppendLog("完整日志：" + result.LogPath);
        }
        foreach (var artifact in result.Artifacts)
        {
            AppendLog("产物：" + artifact);
        }
        lastBuild = result;
    }

    private PortableBuildReport? lastBuild;

    private async Task CloseProjectAsync(CancellationToken token)
    {
        if (!await ConfirmBuffersAsync(buffers, token))
        {
            return;
        }
        await workspace.CloseAsync(token);
        ClearDocuments();
        tree.ItemsSource = null;
        log.Text = "";
        lastBuild = null;
        status.Text = "就绪";
        await RefreshWelcomeAsync(token);
        UpdateControls();
    }

    private async Task RefreshTreeAsync(CancellationToken token)
    {
        if (workspace.Project is not null)
        {
            tree.ItemsSource = await CreateNodesAsync("", token);
        }
    }

    private static Avalonia.Controls.Control FileIcon(string path)
    {
        var color = path == "folder" ? "#D5AE67" : Path.GetExtension(path).ToLowerInvariant() is ".h" or ".hpp" ? "#B393D9" : "#6EA8E5";
        return new WorkbenchIcon(path == "folder" ? "folder" : "file", Avalonia.Media.Brush.Parse(color)) { Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    }
}

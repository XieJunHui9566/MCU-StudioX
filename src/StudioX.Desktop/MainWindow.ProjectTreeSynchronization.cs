namespace StudioX.Desktop;

using System.Windows.Controls;
using StudioX.Application;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private TreeViewItem CreateProjectNode(ProjectEntry item)
    {
        var deviceSupport = item.IsDirectory && item.RelativePath == "device";
        var node = new TreeViewItem
        {
            Header = FileLabel(item.Name, item.IsDirectory, deviceSupport ? " · 器件支持" : ""),
            Tag = item,
            ToolTip = deviceSupport ? "厂商 SDK、寄存器定义、启动文件及内部构建配置；应用代码在 src 中维护。" : item.RelativePath,
            IsEnabled = !item.IsLink
        };
        if (item.IsLink)
        {
            node.ToolTip = item.RelativePath + "（链接目录或文件暂不展开）";
        }
        if (item.IsDirectory && !item.IsLink)
        {
            node.Items.Add(new TreeViewItem { Header = "正在读取…", IsEnabled = false });
            node.Expanded += Directory_Expanded;
            node.Collapsed += (_, args) => { if (args.OriginalSource == node) { SetFolderIcon(node, false); } };
        }
        return node;
    }

    private async Task SynchronizeProjectTreeAsync(ProjectChangeBatch batch, CancellationToken token)
    {
        var selected = SelectedProjectEntry?.RelativePath;
        await Task.WhenAll(loadingProjectDirectories.Values.ToArray()).WaitAsync(token);
        var directories = new List<TreeViewItem>();
        var movedNodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        void Gather(ItemsControl parent)
        {
            foreach (var node in parent.Items.OfType<TreeViewItem>())
            {
                if (node.Tag is not ProjectEntry entry)
                {
                    continue;
                }
                foreach (var rename in batch.Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed))
                {
                    if (ProjectFileService.ContainsPath(rename.PreviousPath!, entry.RelativePath))
                    {
                        var path = rename.Path + entry.RelativePath[rename.PreviousPath!.Length..];
                        entry = entry with
                        {
                            RelativePath = path,
                            Name = Path.GetFileName(path)
                        };
                        node.Tag = entry;
                        movedNodes[path] = node;
                        node.Header = FileLabel(entry.Name, entry.IsDirectory);
                        node.ToolTip = path;
                    }
                }
                if (entry.IsDirectory && loadedProjectDirectories.Contains(node))
                {
                    directories.Add(node);
                    Gather(node);
                }
            }
        }
        Gather(ProjectTree);
        foreach (var rename in batch.Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed))
        {
            if (selected is not null && ProjectFileService.ContainsPath(rename.PreviousPath!, selected))
            {
                selected = rename.Path + selected[rename.PreviousPath!.Length..];
            }
        }
        // 已加载的子树移入尚未展开的目录时，也沿新父路径挂回原节点，保留展开与选择。
        foreach (var rename in batch.Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed))
        {
            if (movedNodes.TryGetValue(rename.Path, out var moved) &&
                (loadedProjectDirectories.Contains(moved) || moved.IsSelected || selected is not null && ProjectFileService.ContainsPath(rename.Path, selected)) &&
                await FindProjectNodeAsync(ProjectFileService.ParentDirectory(rename.Path), token) is { } target)
            {
                await LoadChildrenAsync(target, token);
                target.IsExpanded = moved.IsExpanded || selected is not null && ProjectFileService.ContainsPath(rename.Path, selected) || target.IsExpanded;
            }
        }
        void GatherLoaded(ItemsControl parent)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>().Where(loadedProjectDirectories.Contains))
            {
                if (!directories.Contains(child))
                {
                    directories.Add(child);
                }
                GatherLoaded(child);
            }
        }
        GatherLoaded(ProjectTree);
        foreach (var node in directories)
        {
            token.ThrowIfCancellationRequested();
            var path = ((ProjectEntry)node.Tag).RelativePath;
            if (!batch.RequiresRescan && !batch.Changes.Any(change => ProjectFileService.ParentDirectory(change.Path).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                ProjectFileService.ContainsPath(change.Path, path) ||
                change.PreviousPath is { } previous && ProjectFileService.ParentDirectory(previous).Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            IReadOnlyList<ProjectEntry> entries;
            try
            {
                entries = await services.Files.ListAsync(batch.Directory, path, token);
            }
            catch (DirectoryNotFoundException)
            {
                loadedProjectDirectories.Remove(node);
                continue;
            }
            token.ThrowIfCancellationRequested();
            var existing = node.Items.OfType<TreeViewItem>().Where(child => child.Tag is ProjectEntry)
                .GroupBy(child => ((ProjectEntry)child.Tag).RelativePath, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var desired = entries.Select(entry => (movedNodes.TryGetValue(entry.RelativePath, out var child) || existing.TryGetValue(entry.RelativePath, out child)) && child.Tag is ProjectEntry before &&
                before.IsDirectory == entry.IsDirectory && before.IsLink == entry.IsLink ? child : CreateProjectNode(entry)).ToArray();
            foreach (var obsolete in node.Items.OfType<TreeViewItem>().Except(desired).ToArray())
            {
                if (!movedNodes.Values.Contains(obsolete))
                {
                    RemoveProjectTreeCache(obsolete);
                }
                node.Items.Remove(obsolete);
            }
            for (var i = 0; i < desired.Length; i++)
            {
                var child = desired[i];
                child.Tag = entries[i];
                if (node.Items.IndexOf(child) != i)
                {
                    if (ItemsControl.ItemsControlFromItemContainer(child) is { } owner && !ReferenceEquals(owner, node))
                    {
                        owner.Items.Remove(child);
                    }
                    node.Items.Remove(child);
                    node.Items.Insert(i, child);
                }
                if (entries[i].RelativePath.Equals(selected, StringComparison.OrdinalIgnoreCase))
                {
                    child.IsSelected = true;
                }
                if (i % 128 == 127)
                {
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, token);
                }
            }
        }
        foreach (var orphan in movedNodes.Values.Where(node => ItemsControl.ItemsControlFromItemContainer(node) is null))
        {
            RemoveProjectTreeCache(orphan);
        }
    }

    private void RemoveProjectTreeCache(TreeViewItem node)
    {
        loadedProjectDirectories.Remove(node);
        foreach (var child in node.Items.OfType<TreeViewItem>())
        {
            RemoveProjectTreeCache(child);
        }
    }
}

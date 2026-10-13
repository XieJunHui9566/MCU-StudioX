namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    private void RecentProject_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecentProject recent, ContextMenu: null } button)
        {
            return;
        }
        var menu = new ContextMenu();
        SetMenuColors(menu);
        MenuItem Add(string label, Func<CancellationToken, Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await RunAsync(action);
            menu.Items.Add(item);
            return item;
        }
        Add("在资源管理器中打开所在位置", _ =>
        {
            services.Files.ShowInExplorer(recent.Directory, "");
            Status.Text = "已打开工程所在位置：" + recent.Name;
            return Task.CompletedTask;
        });
        menu.Items.Add(new Separator());
        Add("从最近列表移除", token => RemoveRecentProjectAsync(recent, token));
        var delete = Add("删除工程…", token => DeleteRecentProjectAsync(recent, token));
        menu.Opened += (_, _) =>
        {
            var available = pendingOperation.IsCompleted && !closing;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                item.IsEnabled = available;
            }
            var active = IsRecentProjectOpen(recent.Directory);
            delete.IsEnabled = available && !active;
            delete.ToolTip = active ? "请先关闭此工程，再删除工程文件夹。" : "删除整个工程文件夹；确认后交给 Windows 回收站。";
        };
        button.ContextMenu = menu;
    }

    private bool IsRecentProjectOpen(string directory)
    {
        if (projectDirectory is null)
        {
            return false;
        }
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        return target.Equals(current, HostPlatform.PathComparison) ||
            target.StartsWith(current + Path.DirectorySeparatorChar, HostPlatform.PathComparison) ||
            current.StartsWith(target + Path.DirectorySeparatorChar, HostPlatform.PathComparison);
    }

    private async Task RemoveRecentProjectAsync(RecentProject recent, CancellationToken token)
    {
        await services.RecentProjects.RemoveAsync(recent.Directory, token);
        await RefreshRecentAsync(token);
        Status.Text = "已从最近列表移除，工程文件保留：" + recent.Name;
    }

    private async Task DeleteRecentProjectAsync(RecentProject recent, CancellationToken token, Func<RecentProject, bool>? confirm = null)
    {
        var full = await services.RecentProjects.ValidateDeletionAsync(recent.Directory, projectDirectory, token);
        var accepted = confirm?.Invoke(recent) ?? MessageBox.Show(this,
            $"删除工程“{recent.Name}”？\n\n{full}\n\n将整个工程文件夹移至 Windows 回收站，包含源码、配置和生成文件。\n若无法回收，Windows 会另外提示；可取消该提示以保留文件。",
            "删除工程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!accepted)
        {
            Status.Text = "已取消删除工程。";
            return;
        }
        await services.RecentProjects.DeleteAsync(full, projectDirectory, token);
        await RefreshRecentAsync(CancellationToken.None);
        Status.Text = "已删除工程并移除最近记录：" + recent.Name;
    }
}

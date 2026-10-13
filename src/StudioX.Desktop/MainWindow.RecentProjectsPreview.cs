namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>只回收本次创建的隔离夹具；实际用户列表、工程及回收站其它内容保持原样。</summary>
    public async Task RenderRecentProjectsPreviewAsync(string directory, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        async Task<string> Fixture(string name)
        {
            var target = Path.Combine(directory, name);
            foreach (var path in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c" })
            {
                var file = Path.Combine(target, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, await File.ReadAllBytesAsync(Path.Combine(project, path)));
            }
            await services.RecentProjects.RememberAsync("示例工程", target);
            return target;
        }
        static IEnumerable<Button> Buttons(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is Button button)
                {
                    yield return button;
                }
                foreach (var nested in Buttons(child))
                {
                    yield return nested;
                }
            }
        }
        async Task<(Button Button, ContextMenu Menu)> Menu(string path)
        {
            await RefreshRecentAsync(CancellationToken.None);
            ShowDocument(WelcomeTab);
            await Layout();
            var button = Buttons(RecentProjects).Single(b => b.DataContext is RecentProject p && p.Directory == path);
            button.Focus();
            var menu = button.ContextMenu!;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            await Layout();
            return (button, menu);
        }
        var keep = await Fixture("keep-project");
        var remove = await Fixture("remove-record");
        var recycle = await Fixture("recycle-project");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var (_, menu) = await Menu(recycle);
            Check(menu.Items.OfType<MenuItem>().All(item => item.IsEnabled), theme.Id + " recent menu enabled");
            Check(menu.Items.OfType<MenuItem>().Select(i => i.Header).SequenceEqual(new[] { "在资源管理器中打开所在位置", "从最近列表移除", "删除工程…" }), "row menu has Explorer, remove, delete actions");
            Render(this, Path.Combine(directory, "welcome-" + theme.Id + ".png"));
            Render(menu, Path.Combine(directory, "recent-menu-" + theme.Id + ".png"));
            menu.IsOpen = false;
        }
        var target = new RecentProject("示例工程", recycle, DateTimeOffset.Now);
        await DeleteRecentProjectAsync(target, CancellationToken.None, _ => false);
        Check(File.Exists(Path.Combine(recycle, "src/main.c")) && (await services.RecentProjects.LoadAsync()).Any(p => p.Directory == recycle), "cancelled confirmation preserves files and row");
        var (_, removeMenu) = await Menu(remove);
        var removeItem = removeMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "从最近列表移除"));
        removeMenu.IsOpen = false;
        removeItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await pendingOperation;
        Check(File.Exists(Path.Combine(remove, "src/main.c")) && !(await services.RecentProjects.LoadAsync()).Any(p => p.Directory == remove), "remove record keeps project files");
        await OpenProjectAsync(keep, CancellationToken.None);
        var (_, activeMenu) = await Menu(keep);
        Check(!activeMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "删除工程…")).IsEnabled, "open project deletion disabled");
        activeMenu.IsOpen = false;
        try
        {
            await DeleteRecentProjectAsync(new("示例工程", keep, DateTimeOffset.Now), CancellationToken.None, _ => true);
            throw new InvalidOperationException("Open project deleted.");
        }
        catch (StudioXException ex) when (ex.Code == "PROJECT_DELETE_OPEN") { checks.Add("service also rejects open project deletion"); }
        await CloseProjectAsync(CancellationToken.None);
        await DeleteRecentProjectAsync(target, CancellationToken.None, _ => true);
        Check(!Directory.Exists(recycle) && !(await services.RecentProjects.LoadAsync()).Any(p => p.Directory == recycle) && File.Exists(Path.Combine(keep, "src/main.c")), "confirmed deletion recycles only selected path and refreshes list");
        var (_, remaining) = await Menu(keep);
        Check(remaining.Items.OfType<MenuItem>().All(i => i.IsEnabled), "closed project deletion enabled again");
        remaining.IsOpen = false;
        await RemoveRecentProjectAsync(new("示例工程", keep, DateTimeOffset.Now), CancellationToken.None);
        Check(RecentEmpty.Visibility == Visibility.Visible && File.Exists(Path.Combine(keep, "src/main.c")), "empty welcome state updates after final record removed");
        await JsonStore.WriteAsync(Path.Combine(directory, "recent-result.json"), new
        {
            passed = true,
            checks,
            scope = "isolated fixtures only"
        });
    }
}

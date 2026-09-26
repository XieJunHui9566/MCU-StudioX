namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;

public partial class MainWindow
{
    private double savedProjectWidth = 230;
    private double savedBottomHeight = 150;
    private Task recentPruneTask = Task.CompletedTask;
    private bool recentInitialized;
    private void ShowDocument(TabItem tab)
    {
        tab.Visibility = Visibility.Visible; WorkspaceTabs.SelectedItem = tab;
        if (tab.Tag is EditorDocumentSession session) ActivateEditor(session);
        if (ReferenceEquals(tab, WelcomeTab)) QueueRecentPrune();
    }
    private void Welcome_Click(object sender, RoutedEventArgs e) => ShowDocument(WelcomeTab);
    private async void NewProject_Click(object sender, RoutedEventArgs e) => await RunAsync(token => BeginNewProjectAsync(token));
    public Task ShowNewProjectAsync(string? packId = null, string? deviceId = null) => RunAsync(async token =>
    {
        await BeginNewProjectAsync(token);
        if (projectDirectory is not null) return;
        SelectPack(installedPacks.Where(p => p.Manifest.Id == packId &&
            (deviceId is null || p.Manifest.Devices.Any(device => device.Id == deviceId)))
            .OrderByDescending(p => p.Manifest.Version, Comparer<string>.Create(StudioX.Packages.PackVersion.Compare)).FirstOrDefault());
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<StudioX.Packages.DeviceDefinition>().SingleOrDefault(d => d.Id == deviceId);
        TemplatePicker.SelectedIndex = -1;
    });
    private void Lab_Click(object sender, RoutedEventArgs e) => ShowDocument(LabTab);
    private void SerialPlot_Click(object sender, RoutedEventArgs e) => ShowDocument(SerialPlotTab);
    private void Serial_Click(object sender, RoutedEventArgs e) => ShowDocument(SerialTab);
    private void Extensions_Click(object sender, RoutedEventArgs e) => ShowDocument(ExtensionsTab);
    private void ShowBuildLog_Click(object sender, RoutedEventArgs e) => ShowBottom(0);
    private async void ProjectTerminal_Click(object sender, RoutedEventArgs e)
    {
        savedBottomHeight = Math.Max(savedBottomHeight, 310); ShowBottom(3);
        await ProjectTerminal.EnsureStartedAsync();
    }
    private async void ProjectTerminalTab_GotFocus(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, ProjectTerminalTab)) await ProjectTerminal.EnsureStartedAsync(); }
    private void ShowBottom(int tab)
    {
        BottomRow.Height = new GridLength(savedBottomHeight); BottomPanel.Visibility = Visibility.Visible; BottomTabs.SelectedIndex = tab;
    }
    private void ToggleBottom_Click(object sender, RoutedEventArgs e)
    {
        if (BottomPanel.Visibility == Visibility.Visible)
        {
            savedBottomHeight = Math.Max(100, BottomRow.ActualHeight); BottomRow.Height = new GridLength(0); BottomPanel.Visibility = Visibility.Collapsed;
        }
        else ShowBottom(BottomTabs.SelectedIndex);
    }
    private void ToggleProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectPanel.Visibility == Visibility.Visible)
        {
            savedProjectWidth = Math.Max(180, ProjectColumn.ActualWidth); ProjectColumn.Width = new GridLength(0); ProjectSplitterColumn.Width = new GridLength(0); ProjectPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ProjectColumn.Width = new GridLength(savedProjectWidth); ProjectSplitterColumn.Width = new GridLength(4); ProjectPanel.Visibility = Visibility.Visible;
        }
    }
    private async Task RefreshRecentAsync(CancellationToken token)
    {
        var recent = await services.RecentProjects.LoadAsync(token);
        ShowRecentProjects(recent);
        recentInitialized = true;
    }
    private void ShowRecentProjects(IReadOnlyList<RecentProject> recent)
    {
        RecentProjects.ItemsSource = recent;
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void QueueRecentPrune()
    {
        if (!recentInitialized || closing || !recentPruneTask.IsCompleted) return;
        recentPruneTask = PruneRecentAsync();
    }
    private async Task PruneRecentAsync()
    {
        try
        {
            await services.RecentProjects.PruneMissingLocalAsync();
            if (!closing) ShowRecentProjects(await services.RecentProjects.LoadAsync());
        }
        catch (Exception ex) { if (!closing) Log("最近工程检查失败：" + ex.Message); }
    }
    private async void RecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directory }) return;
        await RunAsync(async token =>
        {
            if (await RemoveMissingRecentAsync(directory, token)) return;
            try { await OpenProjectAsync(directory, token); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (!await RemoveMissingRecentAsync(directory, token)) throw;
            }
        });
    }
    private async Task<bool> RemoveMissingRecentAsync(string directory, CancellationToken token)
    {
        if (!await services.RecentProjects.RemoveIfMissingLocalAsync(directory, token)) return false;
        await RefreshRecentAsync(token);
        Status.Text = "该工程已删除，已从最近工程移除。";
        return true;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, ProductInfo.DisplayName + "\n\n独立单片机开发环境 · 预览版\n\n升级：关闭 IDE 后运行新版安装包，保留工程、器件包和个人设置。\n\n随附器件包：\n" + Path.Combine(AppContext.BaseDirectory, "device-packs"), "关于 MCU StudioX", MessageBoxButton.OK, MessageBoxImage.None);
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Oem3)
        { ProjectTerminal_Click(this, e); e.Handled = true; return; }
        if (ProjectTerminal.IsKeyboardFocusWithin)
        {
            // 工程快捷键不能把终端内的控制输入变成新建、关闭或保存工程。
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.N or Key.O or Key.S or Key.W) e.Handled = true;
            return;
        }
        if (HandleDebugKey(e)) return;
        if (e.Key == Key.F12 && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
        { QueueCodeNavigation(Keyboard.Modifiers == ModifierKeys.Control); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        { _ = RunAsync(_ => TravelNavigationAsync(e.SystemKey == Key.Left)); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W) { CloseDocument_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W) { CloseProject_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S) { SaveAll_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        { CycleEditor(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key == Key.S) { Appearance_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.D1) { ToggleProject_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.D4) { ToggleBottom_Click(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F7) { Build_Click(this, e); e.Handled = true; }
    }
}

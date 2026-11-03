namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ICSharpCode.AvalonEdit;
using StudioX.Engine;

public partial class MainWindow
{
    private readonly BuildAssemblyView buildAssembly = new();
    private Grid? buildAssemblyLayout;
    private BuildAssemblyCatalog? buildAssemblyCatalog;
    private int buildAssemblyRevision;
    private Task buildAssemblyLoadTask = Task.CompletedTask;

    private void InitializeBuildAssemblyPane(Grid editors)
    {
        var parent = (Grid)editors.Parent;
        parent.Children.Remove(editors);
        buildAssemblyLayout = new Grid();
        Grid.SetRow(buildAssemblyLayout, Grid.GetRow(editors));
        Grid.SetColumn(buildAssemblyLayout, Grid.GetColumn(editors));
        Grid.SetRow(editors, 0);
        Grid.SetColumn(editors, 0);
        buildAssemblyLayout.ColumnDefinitions.Add(new());
        buildAssemblyLayout.ColumnDefinitions.Add(new()
        {
            Width = new(0)
        });
        buildAssemblyLayout.ColumnDefinitions.Add(new()
        {
            Width = new(0)
        });
        buildAssemblyLayout.Children.Add(editors);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(BackgroundProperty, "Border");
        Grid.SetColumn(splitter, 1);
        buildAssemblyLayout.Children.Add(splitter);
        Grid.SetColumn(buildAssembly, 2);
        buildAssembly.Visibility = Visibility.Collapsed;
        buildAssemblyLayout.Children.Add(buildAssembly);
        parent.Children.Add(buildAssemblyLayout);
        buildAssembly.Editor.SetBinding(TextEditor.FontFamilyProperty, new Binding("FontFamily") { Source = SourceEditor });
        buildAssembly.Editor.SetBinding(TextEditor.FontSizeProperty, new Binding("FontSize") { Source = SourceEditor });
        buildAssembly.CloseRequested += () => ShowBuildAssembly(false);
        buildAssembly.RefreshRequested += () => buildAssemblyLoadTask = RefreshBuildAssemblyAsync(projectDirectory, true, CancellationToken.None);
        buildAssembly.FileSelected += file => buildAssemblyLoadTask = ReadBuildAssemblyAsync(file, CancellationToken.None);
    }
    private void ShowBuildAssembly(bool visible)
    {
        if (buildAssemblyLayout is null)
        {
            return;
        }
        BuildAssemblyMenu.IsChecked = visible;
        buildAssembly.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        buildAssemblyLayout.ColumnDefinitions[0].Width = new(visible ? 2 : 1, GridUnitType.Star);
        buildAssemblyLayout.ColumnDefinitions[1].Width = new(visible ? 5 : 0);
        buildAssemblyLayout.ColumnDefinitions[2].Width = visible ? new(1, GridUnitType.Star) : new(0);
    }
    private void InvalidateBuildAssembly(string message)
    {
        buildAssemblyRevision++;
        buildAssemblyCatalog = null;
        buildAssembly.SetMessage(message);
    }
    private Task RefreshVisibleBuildAssemblyAsync(string root, CancellationToken token)
    {
        // 编译完成只刷新用户已打开的面板，不改变编辑区布局或重新打开已关闭的面板。
        return buildAssembly.Visibility == Visibility.Visible
            ? RefreshBuildAssemblyAsync(root, false, token) : Task.CompletedTask;
    }
    private async void BuildAssembly_Click(object sender, RoutedEventArgs e)
    {
        if (buildAssembly.Visibility == Visibility.Visible)
        {
            ShowBuildAssembly(false);
            return;
        }
        buildAssemblyLoadTask = RefreshBuildAssemblyAsync(projectDirectory, true, CancellationToken.None);
        await buildAssemblyLoadTask;
    }
    private async Task RefreshBuildAssemblyAsync(string? root, bool show, CancellationToken token)
    {
        if (!BuildAssemblyService.Supports(currentProjectManifest) || root is null || root != projectDirectory)
        {
            return;
        }
        if (show)
        {
            ShowBuildAssembly(true);
        }
        InvalidateBuildAssembly("正在读取本次构建的汇编文件…");
        var revision = buildAssemblyRevision;
        try
        {
            if (editorDocuments.Any(document => document.IsDirty))
            {
                buildAssembly.SetMessage("源码存在未保存的修改，请保存并重新编译后查看汇编。");
                return;
            }
            var catalog = await services.BuildAssembly.ListAsync(root, token);
            if (root != projectDirectory || revision != buildAssemblyRevision)
            {
                return;
            }
            buildAssemblyCatalog = catalog;
            buildAssembly.SetFiles(catalog, activeEditor?.Source.RelativePath);
            if (buildAssembly.SelectedFile is { } file)
            {
                await ReadBuildAssemblyAsync(file, token);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (root == projectDirectory && revision == buildAssemblyRevision)
            {
                buildAssembly.SetMessage(error.Message);
                Log("汇编查看：" + error);
            }
        }
    }
    private async Task ReadBuildAssemblyAsync(BuildAssemblyFile file, CancellationToken token)
    {
        if (projectDirectory is not { } root || buildAssemblyCatalog is not { } catalog)
        {
            return;
        }
        var revision = ++buildAssemblyRevision;
        buildAssembly.SetLoading();
        try
        {
            var document = await services.BuildAssembly.ReadAsync(root, catalog, file, token);
            if (root == projectDirectory && revision == buildAssemblyRevision)
            {
                buildAssembly.SetDocument(document, catalog.DeviceId);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (root == projectDirectory && revision == buildAssemblyRevision)
            {
                buildAssembly.SetMessage(error.Message);
                Log("汇编查看：" + error);
            }
        }
    }
}

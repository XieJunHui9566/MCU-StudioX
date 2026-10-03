namespace StudioX.Desktop;

using System.Windows;
using Microsoft.Win32;
using StudioX.Packages;
using StudioX.Foundation;

public partial class MainWindow
{
    private async Task CreateComponentMigrationAsync(DistributionCenterView view, CancellationToken token)
    {
        var source = RequireProject();
        await SaveAllSourcesAsync(source, token);
        var targets = await services.ComponentMigration.ListTargetsAsync(source, token);
        if (targets.Count == 0) throw new StudioXException("TOOLS_MIGRATION_PACK", "请先导入同一器件和模板的新版 .mcupack，再创建升级验证副本。安装新工具不会自动修改原器件包。");
        var picker = new QuickPickWindow("明确选择升级验证使用的器件包版本", (query, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(
            targets.Where(p => (p.Manifest.Id + " " + p.Manifest.Version).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(p => new QuickPickItem(p.Manifest.DisplayName + " · " + p.Manifest.Version, p.Manifest.Id + " · " + p.ContentHash, p)).ToArray())) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected?.Value is not InstalledPack pack) return;
        var parent = new OpenFolderDialog { Title = "选择副本父目录；将创建新的工程子目录" };
        if (parent.ShowDialog(this) != true) return;
        var suggestedName = await services.ComponentMigration.SuggestDirectoryNameAsync(source, token);
        var name = new EntryNameWindow("验证副本目录名称", "创建新目录，不覆盖原工程。", suggestedName, directory: true) { Owner = this };
        if (name.ShowDialog() != true) return;
        var destination = PathBoundary.Resolve(parent.FolderName, name.EntryName);
        var preview = await services.ComponentMigration.PreviewAsync(source, pack, destination, token);
        view.SetDetail(preview.ToText());
        if (!preview.CanCreate) throw new StudioXException("TOOLS_MIGRATION_REVIEW", preview.ToText());
        if (MessageBox.Show(this, preview.ToText(), "创建并编译验证副本", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var result = await services.ComponentMigration.CreateAndBuildAsync(preview, new Progress<string>(view.SetStatus), token);
        view.SetDetail(result.ToText());
        view.SetStatus(result.Success ? "副本编译通过；原工程保留。可从“文件 → 打开工程”检查副本。" : "副本验证未完成，详细诊断已保留；原工程仍可打开。 ");
        Log(result.ToText());
    }
}

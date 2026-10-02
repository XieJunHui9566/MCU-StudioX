namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Tools;
using StudioX.Foundation;

public partial class MainWindow
{
    private TabItem? projectToolsTab;
    private ProjectToolPreparationView? projectToolsView;
    private string? projectToolsDirectory;
    private string? projectToolsKey;

    private Task ShowProjectToolsAsync(string? directory = null) => RunAsync(async token =>
    {
        projectToolsDirectory = directory ?? projectDirectory;
        if (projectToolsTab is null)
        {
            projectToolsView = new() { Requested = RunProjectToolsAsync, SelectionChanged = ShowProjectToolDetail };
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtime/distribution/catalog.json");
            if (File.Exists(bundled)) projectToolsView.Source = bundled;
            projectToolsTab = AddToolTab("准备工程工具", projectToolsView);
        }
        ShowDocument(projectToolsTab);
        await RefreshProjectToolsAsync(token);
    });

    private async Task RefreshProjectToolsAsync(CancellationToken token)
    {
        projectToolsView!.SetBusy(true);
        projectToolsView.InvalidatePlan();
        try { projectToolsView.SetPlan(await services.ProjectTools.InspectAsync(projectToolsDirectory, projectToolsView.Listing, token)); }
        catch (Exception error)
        {
            projectToolsView.InvalidatePlan("本次检查没有完成；请核对工程后重新检查。");
            projectToolsView.SetDetail(error.ToString());
            throw;
        }
        finally { projectToolsView.SetBusy(false); }
    }

    private void ShowProjectToolDetail(ProjectToolRequirement item)
    {
        var text = $"{item.Id} / {item.Version} · {item.CompilerId}\n{item.Diagnostic}\n";
        if (item.LockedFingerprint is { } fingerprint) text += "工程内容锁：" + fingerprint + "\n";
        if (item.Entry is { } entry)
        {
            text += $"\n{entry.Name}\n来源：{entry.SourceUrl}\n许可证：{entry.License}\nSHA-256：{entry.Sha256}\n下载 {entry.DownloadBytes:N0} 字节；展开 {entry.InstalledBytes:N0} 字节\n"
                + services.Distribution.DownloadState(entry).ToText() + "\n"
                + string.Join('\n', services.Distribution.SpacePlan(entry, services.Toolsets.RootDirectory).Select(p => $"磁盘 {p.Directory}：需要 {p.RequiredBytes:N0} 字节（含缓存和暂存），可用 {p.AvailableBytes:N0} 字节"))
                + "\n更新说明：" + entry.ReleaseNotes;
        }
        else if (item.State == ProjectToolState.Missing) text += "\n当前目录没有精确版本。请读取合适的目录，或导入相同 ID、版本与编译器的 .studioxtools 归档。";
        projectToolsView!.SetDetail(text);
    }

    private Task RunProjectToolsAsync(string action)
    {
        if (action == "create") return RunAsync(token => BeginNewProjectAsync(token));
        if (action == "help") return ShowHelpAsync("distribution");
        if (action == "management") return ShowToolManagementAsync(projectToolsDirectory);
        if (action == "repair") return ShowToolEnvironmentForProjectAsync(projectToolsDirectory);
        return RunAsync(async token =>
        {
            var view = projectToolsView!;
            var acceptingProgress = true;
            var progress = new Progress<string>(text => { if (acceptingProgress) view.SetStatus(text); });
            view.SetBusy(true);
            try
            {
                if (action == "project")
                {
                    var dialog = new OpenFolderDialog { Title = "选择要准备工具的 StudioX 工程" };
                    if (dialog.ShowDialog(this) != true) return;
                    projectToolsDirectory = dialog.FolderName;
                }
                if (action == "browse")
                {
                    var dialog = new OpenFileDialog { Filter = "分发目录|*.json" };
                    if (dialog.ShowDialog(this) != true) return;
                    view.Source = dialog.FileName; action = "load";
                }
                if (action is "key" or "clear-key")
                {
                    if (action == "key")
                    {
                        var dialog = new OpenFileDialog { Filter = "RSA 发布者公钥|*.pem" };
                        if (dialog.ShowDialog(this) != true) return;
                        projectToolsKey = dialog.FileName;
                    }
                    else projectToolsKey = null;
                    // 更换密钥后旧目录不能继续沿用旧验证标记和可下载选择。
                    view.SetListing(null);
                    view.SetStatus("来源验证设置已变更，请重新读取目录。公钥须通过独立可信渠道确认。");
                }
                if (action == "load")
                {
                    view.SetListing(null);
                    view.SetListing(await services.Distribution.ReadAsync(view.Source.Trim(), projectToolsKey, token));
                }
                if (action is "download" or "offline")
                {
                    var plan = view.Plan ?? throw new StudioXException("TOOLS_SELECTION", "请先检查工程。");
                    var item = view.Selected ?? throw new StudioXException("TOOLS_SELECTION", "请选择缺少的工具集。");
                    string archive;
                    if (action == "offline")
                    {
                        var dialog = new OpenFileDialog { Title = $"导入 {item.Id}/{item.Version}", Filter = "StudioX 离线工具包|*.studioxtools" };
                        if (dialog.ShowDialog(this) != true) return;
                        archive = dialog.FileName;
                    }
                    else
                    {
                        var entry = item.Entry ?? throw new StudioXException("TOOLS_SELECTION", "目录没有工程需要的精确版本。");
                        if (services.Distribution.SpacePlan(entry, services.Toolsets.RootDirectory).Any(p => p.RequiredBytes > p.AvailableBytes))
                            throw new StudioXException("INSTALL_SPACE", "下载缓存和安装暂存所需的磁盘空间不足。");
                        archive = await services.Distribution.DownloadAsync(view.Listing!, entry, progress, token);
                    }
                    var preview = await services.ProjectTools.PreviewAsync(plan, item, archive, progress, token, verifyCatalog: action == "download");
                    var space = services.ProjectTools.InstallSpacePlan(preview);
                    if (space.Any(p => p.RequiredBytes > p.AvailableBytes)) throw new StudioXException("INSTALL_SPACE", "安装暂存空间不足。");
                    var origin = action == "download" ? $"\n来源：{item.Entry!.SourceUrl}\n许可证：{item.Entry.License}\n{view.Listing!.Verification}" : "\n来源：用户选择的本地归档，未验证发布者签名。";
                    if (MessageBox.Show(this, preview.ToText() + $"\n归档：{preview.Archive}\nSHA-256：{preview.ArchiveSha256}" + origin
                        + "\n\n此安装匹配当前工程指定版本，工程锁保持不变。", "预览工程工具安装", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    await services.ProjectTools.InstallAsync(plan, item, preview, progress, token, verifyCatalog: action == "download");
                    acceptingProgress = false;
                    view.SetStatus("安装完成。当前工程锁保持不变；请运行健康检查或编译验证。");
                }
                acceptingProgress = false;
                await RefreshProjectToolsAsync(token);
            }
            catch (Exception error)
            {
                acceptingProgress = false;
                view.SetStatus(error is OperationCanceledException ? "操作已取消；已落盘的下载进度保留，下次可继续。" : error.Message);
                view.SetDetail(error.ToString());
                throw;
            }
            finally { acceptingProgress = false; view.SetBusy(false); }
        });
    }
}

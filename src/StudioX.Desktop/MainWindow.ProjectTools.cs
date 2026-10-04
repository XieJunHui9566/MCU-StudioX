namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Distribution;
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
            projectToolsView = new()
            {
                Requested = RunProjectToolsAsync,
                SelectionChanged = ShowProjectToolDetail
            };
            var bundled = Path.Combine(AppContext.BaseDirectory, "runtime/distribution/catalog.json");
            if (File.Exists(bundled))
            {
                projectToolsView.Source = bundled;
            }
            projectToolsTab = AddToolTab("准备工程开发环境组件", projectToolsView);
        }
        ShowDocument(projectToolsTab);
        await RefreshProjectToolsAsync(token);
    });

    private async Task RefreshProjectToolsAsync(CancellationToken token)
    {
        projectToolsView!.SetBusy(true);
        projectToolsView.InvalidatePlan();
        try
        {
            projectToolsView.SetPlan(await services.ProjectTools.InspectAsync(projectToolsDirectory, projectToolsView.Listing, token));
        }
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
        if (item.LockedFingerprint is { } fingerprint)
        {
            text += "工程内容锁：" + fingerprint + "\n";
        }
        if (item.Entry is { } entry)
        {
            text += $"\n{entry.Name}\n来源：{entry.SourceUrl}\n许可证：{entry.License}\nSHA-256：{entry.Sha256}\n下载 {entry.DownloadBytes:N0} 字节；展开 {entry.InstalledBytes:N0} 字节\n"
                + services.Distribution.DownloadState(entry).ToText() + "\n"
                + string.Join('\n', services.Distribution.SpacePlan(entry, services.Toolsets.RootDirectory).Select(p => $"磁盘 {p.Directory}：需要 {p.RequiredBytes:N0} 字节（含缓存和暂存），可用 {p.AvailableBytes:N0} 字节"))
                + "\n更新说明：" + entry.ReleaseNotes;
        }
        else if (item.State == ProjectToolState.Missing)
        {
            text += "\n可点击自动获取，按工程要求读取 GitHub 组件目录；或切换手动模式导入相同 ID、版本与编译器的 .mcutoolchain。尚未发布的版本不会自动换用其他版本。";
        }
        projectToolsView!.SetDetail(text);
    }

    private Task RunProjectToolsAsync(string action)
    {
        if (action == "create")
        {
            return RunAsync(token => BeginNewProjectAsync(token));
        }
        if (action == "help")
        {
            return ShowHelpAsync("distribution");
        }
        if (action == "management")
        {
            return ShowToolManagementAsync(projectToolsDirectory);
        }
        if (action == "repair")
        {
            return ShowToolEnvironmentForProjectAsync(projectToolsDirectory);
        }
        if (action == "cancel")
        {
            operationCancellation?.Cancel();
            return Task.CompletedTask;
        }
        if (action == "library")
        {
            return ShowGithubComponentLibraryAsync();
        }
        return RunAsync(async token =>
        {
            var view = projectToolsView!;
            var acceptingProgress = true;
            var progress = new Progress<string>(text => { if (acceptingProgress) { view.SetStatus(text); } });
            view.SetBusy(true);
            try
            {
                if (action == "project")
                {
                    var dialog = new OpenFolderDialog { Title = "选择要准备工具的 StudioX 工程" };
                    if (dialog.ShowDialog(this) != true)
                    {
                        return;
                    }
                    projectToolsDirectory = dialog.FolderName;
                }
                if (action == "browse")
                {
                    var dialog = new OpenFileDialog { Filter = "分发目录|*.json" };
                    if (dialog.ShowDialog(this) != true)
                    {
                        return;
                    }
                    view.Source = dialog.FileName;
                    action = "load";
                }
                if (action is "key" or "clear-key")
                {
                    if (action == "key")
                    {
                        var dialog = new OpenFileDialog { Filter = "RSA 发布者公钥|*.pem" };
                        if (dialog.ShowDialog(this) != true)
                        {
                            return;
                        }
                        projectToolsKey = dialog.FileName;
                    }
                    else
                    {
                        projectToolsKey = null;
                    }
                    // 更换密钥后旧目录不能继续沿用旧验证标记和可下载选择。
                    view.SetListing(null);
                    view.SetStatus("来源验证设置已变更，请重新读取目录。公钥须通过独立可信渠道确认。");
                }
                if (action is "load" or "trusted")
                {
                    view.SetListing(null);
                    if (action == "trusted")
                    {
                        view.Source = TrustedDevelopmentCatalog.Source;
                    }
                    view.SetStatus("正在读取目录并校验发布者签名…");
                    view.SetListing(action == "trusted" ? await services.Distribution.ReadTrustedAsync(token)
                        : await services.Distribution.ReadAsync(view.Source.Trim(), projectToolsKey, token));
                }
                if (action == "github-acquire")
                {
                    var plan = view.Plan ?? throw new StudioXException("TOOLS_SELECTION", "请先检查工程。");
                    view.Source = TrustedDevelopmentCatalog.Source;
                    view.SetListing(null);
                    var result = await services.ProjectTools.AcquireFromGithubAsync(plan, services.Distribution, progress, token);
                    acceptingProgress = false;
                    view.SetListing(result.Listing);
                    view.SetPlan(result.Plan);
                    view.SetStatus(result.Installed.Count == 0 ? "所需组件已就绪，无需下载。请运行健康检查或编译验证。"
                        : $"已安装 {result.Installed.Count} 个组件。工程版本与内容锁保持不变；请运行健康检查或编译验证。");
                    return;
                }
                if (action == "offline")
                {
                    var plan = view.Plan ?? throw new StudioXException("TOOLS_SELECTION", "请先检查工程。");
                    var dialog = new OpenFileDialog { Title = "选择工程所需开发环境组件，可多选", Filter = DevelopmentComponentDialogs.ImportFilter, Multiselect = true };
                    if (dialog.ShowDialog(this) != true)
                    {
                        return;
                    }
                    var previews = await services.ProjectTools.PreviewManualAsync(plan, dialog.FileNames, progress, token);
                    var description = string.Join("\n\n", previews.Select(p => p.ToText() + $"\n归档：{p.Archive}\nSHA-256：{p.ArchiveSha256}"));
                    if (MessageBox.Show(this, description + "\n\n来源：用户选择的本地归档，未验证发布者签名。确认后完整校验并安装，工程配置与内容锁保持不变。",
                        "预览本地组件导入", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    var result = await services.ProjectTools.InstallManualAsync(plan, previews, progress, token);
                    acceptingProgress = false;
                    view.SetPlan(result.Plan);
                    view.SetStatus($"已导入 {result.Installed.Count} 个组件。工程版本与内容锁保持不变；请运行健康检查或编译验证。");
                    return;
                }
                if (action == "download")
                {
                    var plan = view.Plan ?? throw new StudioXException("TOOLS_SELECTION", "请先检查工程。");
                    var item = view.Selected ?? throw new StudioXException("TOOLS_SELECTION", "请选择缺少的开发环境组件。");
                    var entry = item.Entry ?? throw new StudioXException("TOOLS_SELECTION", "目录没有工程需要的精确版本。");
                    if (services.Distribution.SpacePlan(entry, services.Toolsets.RootDirectory).Any(p => p.RequiredBytes > p.AvailableBytes))
                    {
                        throw new StudioXException("INSTALL_SPACE", "下载缓存和安装暂存所需的磁盘空间不足。");
                    }
                    var archive = await services.Distribution.DownloadAsync(view.Listing!, entry, progress, token);
                    var preview = await services.ProjectTools.PreviewAsync(plan, item, archive, progress, token);
                    var space = services.ProjectTools.InstallSpacePlan(preview);
                    if (space.Any(p => p.RequiredBytes > p.AvailableBytes))
                    {
                        throw new StudioXException("INSTALL_SPACE", "安装暂存空间不足。");
                    }
                    var origin = $"\n来源：{entry.SourceUrl}\n许可证：{entry.License}\n{view.Listing!.Verification}";
                    if (MessageBox.Show(this, preview.ToText() + $"\n归档：{preview.Archive}\nSHA-256：{preview.ArchiveSha256}" + origin
                        + "\n\n此安装匹配当前工程指定版本，工程锁保持不变。", "预览工程工具安装", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    await services.ProjectTools.InstallAsync(plan, item, preview, progress, token);
                    acceptingProgress = false;
                    view.SetStatus("安装完成。当前工程锁保持不变；请运行健康检查或编译验证。");
                }
                acceptingProgress = false;
                await RefreshProjectToolsAsync(token);
            }
            catch (Exception error)
            {
                acceptingProgress = false;
                // 多组件安装可能已有部分完成；取消后也重新读取本地状态，不能留着旧的“未安装”。
                if (action is "github-acquire" or "offline")
                {
                    try
                    {
                        await RefreshProjectToolsAsync(CancellationToken.None);
                    }
                    catch (Exception refreshError) { Log(refreshError.ToString()); }
                }
                view.SetStatus(error is OperationCanceledException ? "操作已取消；已安装组件与已落盘下载进度保留，下次可继续。" : error.Message);
                view.SetDetail(error.ToString());
                throw;
            }
            finally { acceptingProgress = false; view.SetBusy(false); }
        });
    }
}

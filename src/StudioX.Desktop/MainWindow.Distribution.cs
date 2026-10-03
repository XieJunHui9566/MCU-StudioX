namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Components;
using StudioX.Application.Distribution;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private DistributionCenterView? distributionView;
    private TabItem? distributionTab;
    private string? distributionKey;
    private Task ShowDistributionAsync()
    {
        if (distributionTab is null)
        {
            distributionView = new()
            {
                Requested = RunDistributionAsync,
                SelectionChanged = ShowDistributionDetail
            };
            var bundledCatalog = Path.Combine(AppContext.BaseDirectory, "runtime/distribution/catalog.json");
            if (File.Exists(bundledCatalog))
            {
                distributionView.Source = bundledCatalog;
            }
            distributionTab = AddToolTab("软件与组件分发", distributionView);
        }
        ShowDocument(distributionTab);
        return Task.CompletedTask;
    }
    private async Task ShowGithubComponentLibraryAsync()
    {
        await ShowDistributionAsync();
        await RunDistributionAsync("trusted");
    }
    private void ShowDistributionDetail(DistributionEntry entry)
    {
        var destination = entry.Kind == "tool" ? services.Toolsets.RootDirectory : entry.Kind == "plugin" ? services.DataDirectory : projectDirectory ?? services.DataDirectory;
        try
        {
            var plans = services.Distribution.SpacePlan(entry, destination);
            distributionView!.SetDetail($"{entry.Name} · {entry.Id}/{entry.Version}\n来源：{entry.SourceUrl}\n许可证：{entry.License}\n归档 SHA-256：{entry.Sha256}\n下载：{entry.DownloadBytes:N0} 字节；展开：{entry.InstalledBytes:N0} 字节\n"
                + string.Join("\n", plans.Select(p => $"磁盘 {p.Directory}：预计需要 {p.RequiredBytes:N0} 字节（含暂存），可用 {p.AvailableBytes:N0} 字节"))
                + $"\n插件 API：{entry.PluginApi}；框架：{string.Join(", ", entry.Frameworks ?? [])}\n更新说明：{entry.ReleaseNotes}");
        }
        catch (Exception error) { distributionView!.SetDetail(error.ToString()); }
    }
    private Task RunDistributionAsync(string action) => action == "required" ? ShowProjectToolsAsync() : RunAsync(async token =>
    {
        var view = distributionView!;
        view.SetBusy(true);
        try
        {
            if (action == "migration") { await CreateComponentMigrationAsync(view, token); return; }
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
            if (action == "key")
            {
                var dialog = new OpenFileDialog { Filter = "RSA 发布者公钥|*.pem" };
                if (dialog.ShowDialog(this) == true)
                {
                    distributionKey = dialog.FileName;
                    view.SetListing(null);
                    view.SetStatus("自定义目录公钥已变更，请重新读取。已验证组件库始终使用 IDE 内置公钥。");
                }
                return;
            }
            if (action == "clear-key")
            {
                distributionKey = null;
                view.SetListing(null);
                view.SetStatus("已取消自定义公钥，请重新读取目录。已验证组件库仍强制验签。");
                return;
            }
            if (action is "load" or "trusted")
            {
                view.SetListing(null);
                if (action == "trusted") view.Source = TrustedDevelopmentCatalog.Source;
                view.SetStatus("正在读取目录并校验发布者签名…");
                view.SetListing(action == "trusted" ? await services.Distribution.ReadTrustedAsync(token)
                    : await services.Distribution.ReadAsync(view.Source.Trim(), distributionKey, token));
                return;
            }
            if (action == "components")
            {
                var components = await services.Components.ReadAsync(RequireProject(), token);
                view.SetDetail(components is null ? "本工程没有通过组件管理页添加的组件。" : string.Join("\n\n", components.Components.Select(c => $"{c.Manifest.Name} · {c.Manifest.Id}/{c.Manifest.Version}\n许可证 {c.Manifest.License}\n来源 {c.Manifest.SourceUrl}\n归档 SHA-256 {c.ArchiveSha256}\n目录 {c.RelativeDirectory}")));
                return;
            }
            if (action == "component-rollback")
            {
                var project = RequireProject();
                var components = await services.Components.ReadAsync(project, token);
                var revision = components?.History.LastOrDefault() ?? throw new StudioXException("COMPONENT_HISTORY", "没有可回退的组件版本。");
                if (MessageBox.Show(this, "将恢复以下组件版本，旧源码目录保留：\n" + string.Join("\n", revision.Components.Select(c => c.Manifest.Id + "/" + c.Manifest.Version)), "确认组件回退", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
                await SaveAllSourcesAsync(project, token);
                await services.Components.RollbackAsync(project, token);
                view.SetStatus("已恢复上一组组件版本；请重新编译。");
                return;
            }
            if (action == "plugin-rollback")
            {
                var id = view.Selected is { Kind: "plugin" } plugin ? plugin.Id : throw new StudioXException("PLUGIN_SELECTION", "请选择需要回退的插件条目。");
                var versions = await services.PluginManager.ListRollbackAsync(id, token);
                var picker = new QuickPickWindow("选择回退版本（安装后需重新启用）", (query, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(versions.Where(v => v.Version.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(v => new QuickPickItem(v.Version, v.Sha256, v)).ToArray())) { Owner = this };
                if (picker.ShowDialog() != true || picker.Selected?.Value is not StudioX.Application.Plugins.PluginRollbackVersion selected)
                {
                    return;
                }
                await services.PluginManager.RollbackAsync(selected, token);
                await ReloadPluginWorkspaceAsync(token);
                view.SetStatus("已回退插件；到插件管理页审阅并重新启用。");
                return;
            }
            string archive;
            DistributionEntry? item = null;
            if (action == "component")
            {
                var dialog = new OpenFileDialog { Filter = "StudioX 组件|*.studioxcomponent" };
                if (dialog.ShowDialog(this) != true)
                {
                    return;
                }
                archive = dialog.FileName;
            }
            else if (action is "install" or "preview-tool" && view.Listing is { } listing && view.Selected is { } selected)
            {
                item = selected;
                var destination = item.Kind == "tool" ? services.Toolsets.RootDirectory : item.Kind == "component" ? RequireProject() : services.DataDirectory;
                if (services.Distribution.SpacePlan(item, destination).Any(p => p.RequiredBytes > p.AvailableBytes))
                {
                    throw new StudioXException("INSTALL_SPACE", "磁盘空间不足，请先释放空间。");
                }
                archive = await services.Distribution.DownloadAsync(listing, item, new Progress<string>(view.SetStatus), token);
            }
            else
            {
                return;
            }
            if (item?.Kind == "tool")
            {
                var preview = await services.ToolManagement.PreviewInstallAsync(archive, token: token);
                if (preview.Id != item.Id || preview.Version != item.Version || preview.Bytes != item.InstalledBytes)
                {
                    throw new StudioXException("CATALOG_IDENTITY", "工具归档与目录声明不一致。");
                }
                var plan = projectDirectory is null ? null : await services.ProjectTools.InspectAsync(projectDirectory, token: token);
                var compatibility = await services.ProjectTools.PreviewCompatibilityAsync(plan, preview, token);
                var description = compatibility.ToText() + "\n\n" + preview.ToText() + "\n\n" + view.Listing!.Verification;
                view.SetDetail(description);
                if (action == "preview-tool") { view.SetStatus("升级预览完成。尚未安装或切换工程版本。"); return; }
                if (!compatibility.CanInstall) throw new StudioXException("TOOLS_PROJECT_IDENTITY", compatibility.ToText());
                if (MessageBox.Show(this, description, "确认开发环境组件", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
                await services.ProjectTools.InstallWithCompatibilityAsync(plan, preview, compatibility, new Progress<string>(view.SetStatus), token);
                Log($"开发环境组件 {preview.Identity.Key}：并存安装或重复完整校验完成，当前工程仍保持原需求。");
            }
            else if (item?.Kind == "plugin")
            {
                await DistributionService.ValidatePluginArchiveAsync(archive, item, token);
                if (MessageBox.Show(this, $"安装 {item.Name}/{item.Version}\n{item.ReleaseNotes}\n{view.Listing!.Verification}\n安装不自动启用，更新前保留可回退版本。", "确认插件安装", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
                await services.PluginManager.ImportAsync(archive, token);
                await ReloadPluginWorkspaceAsync(token);
            }
            else
            {
                var preview = await services.Components.PreviewAsync(archive, token);
                if (item is not null && (preview.Manifest.Id != item.Id || preview.Manifest.Version != item.Version || preview.Bytes != item.InstalledBytes))
                {
                    throw new StudioXException("CATALOG_IDENTITY", "组件归档与目录声明不一致。");
                }
                var project = RequireProject();
                var description = $"{preview.Manifest.Name} · {preview.Manifest.Id}/{preview.Manifest.Version}\n许可证：{preview.Manifest.License}\n来源：{preview.Manifest.SourceUrl}\n展开 {preview.Bytes:N0} 字节\n源码：{string.Join(", ", preview.Manifest.Sources)}\n头文件：{string.Join(", ", preview.Manifest.IncludeDirectories)}\nCMake 目标：{view.ComponentTarget}\n原生工程将在 CMakeLists.txt 末尾加入受控 include；ESP-IDF 生成 components 下的注册文件，MINIMAL_BUILD 工程需在 main REQUIRES 声明组件。\n版本和哈希保存到 studiox-components.lock.json；旧源码保留以便回退。";
                view.SetDetail(description);
                if (MessageBox.Show(this, description, "确认加入工程", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    return;
                }
                await SaveAllSourcesAsync(project, token);
                await services.Components.InstallAsync(project, preview, view.ComponentTarget, token);
            }
            view.SetStatus("安装完成。组件需要重新编译；插件请到管理页确认启用；已有开发环境组件版本锁定保持不变。");
        }
        catch (Exception error) { view.SetDetail(error.ToString()); throw; }
        finally { view.SetBusy(false); }
    });
}

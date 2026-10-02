namespace StudioX.Application.Tools;

using StudioX.Engine;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;

public sealed partial class ToolManagementService
{
    private async Task<(Dictionary<string, List<string>> Items, IReadOnlyList<string> Projects)> ReadReferencesAsync(
        string? currentProject, List<string> diagnostics, CancellationToken token)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string version, string reason)
        {
            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(version)) return;
            StudioX.Packages.PackValidator.Token(id); StudioX.Packages.PackValidator.Version(version);
            var key = id + "/" + version;
            if (!references.TryGetValue(key, out var entries)) references[key] = entries = [];
            entries.Add(reason);
        }
        // 这些入口在运行时明确锁定版本，不能因为尚未建立工程就被当作闲置版本。
        Add(CubeMxImportService.ToolsetId, CubeMxImportService.ToolsetVersion, "IDE 功能：CubeMX 工程导入");
        Add(LvglPreviewBuilder.BundledToolsetId, LvglPreviewBuilder.BundledToolsetVersion, "IDE 功能：LVGL PC 预览");
        Add("agm.pin-mapping", "1.0.0", "IDE 功能：AG32 引脚与逻辑映射");
        Add("agm.logic", "1.0.0", "IDE 功能：AG32 原生逻辑构建");
        Add("hdl.iverilog", "14.0.0", "IDE 功能：RTL 仿真");
        try { foreach (var item in await recent.LoadAsync(token)) projects.Add(Path.GetFullPath(item.Directory)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { diagnostics.Add("最近工程记录无法读取：\n" + error); }
        try { foreach (var item in await RegisteredAsync(token)) projects.Add(Path.GetFullPath(item)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { diagnostics.Add("登记工程记录无法读取：\n" + error); }
        if (currentProject is not null) projects.Add(Path.GetFullPath(currentProject));
        foreach (var project in projects.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ProjectService.ReadAsync(project, token);
                Add(manifest.ToolsetId, manifest.ToolsetVersion, "工程：" + manifest.Name + " · " + project);
                if (manifest.PinMapping is { } mapping) Add(mapping.ToolsetId, mapping.ToolsetVersion, "工程引脚映射：" + manifest.Name + " · " + project);
                foreach (var relative in new[] { ".studiox/toolchain.lock.json", ".studiox/ag32-mapping-toolchain.lock.json", ".studiox/ag32-logic-toolchain.lock.json" })
                {
                    var path = PathBoundary.Resolve(project, relative);
                    if (!File.Exists(path)) continue;
                    var locked = await JsonStore.ReadAsync<ToolchainLock>(path, token);
                    if (locked.FormatVersion != 1) throw new StudioXException("TOOLS_PROJECT_LOCK", "工具内容锁格式不支持：" + path);
                    Add(locked.ToolsetId, locked.ToolsetVersion, "内容锁：" + project + " / " + relative);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { diagnostics.Add("工程依赖无法核实：" + project + "\n" + error); }
        }
        try
        {
            foreach (var pack in await packs.ListCatalogAsync(token))
                foreach (var device in pack.Manifest.Devices)
                    Add(device.ToolsetId, device.ToolsetVersion, "器件包：" + pack.Manifest.Id + " / " + pack.Manifest.Version);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { diagnostics.Add("器件包依赖无法核实：\n" + error); }
        return (references, projects.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>在明确选择的新目录中验证升级，永远不改写原工程、不连接硬件。</summary>
public sealed partial class ComponentMigrationService(PackRepository packs, BuildService builds)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string> SuggestDirectoryNameAsync(string sourceDirectory, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(sourceDirectory, token);
        var name = Path.GetFileName(Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar));
        if (project.Espressif is not null)
        {
            name = System.Text.RegularExpressions.Regex.Replace(name, "[^A-Za-z0-9._-]", "-").Trim('-', '.');
            if (name.Length == 0)
            {
                name = "project";
            }
        }
        return name + "-upgrade";
    }
    public async Task<IReadOnlyList<InstalledPack>> ListTargetsAsync(string sourceDirectory, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(sourceDirectory, token);
        EnsureProfile(project);
        return (await packs.ListCatalogAsync(token)).Where(p => p.Manifest.Id == project.PackId && p.Manifest.Version != project.PackVersion
            && p.Manifest.Devices.Any(d => d.Id == project.DeviceId && d.Templates.Any(t => t.Id == project.TemplateId))).ToArray();
    }
    public Task<ComponentMigrationPreview> PreviewAsync(string sourceDirectory, InstalledPack targetPack, string destinationDirectory,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        RejectLinkedAncestors(root);
        ValidateDestination(root, destination, targetPack.RootDirectory);
        var project = await ProjectService.ReadAsync(root, token);
        EnsureProfile(project);
        var before = await CaptureAsync(root, token);
        var oldPack = (await packs.ListCatalogAsync(token)).SingleOrDefault(p => p.Manifest.Id == project.PackId && p.Manifest.Version == project.PackVersion
            && p.ContentHash.Equals(project.PackContentHash, StringComparison.OrdinalIgnoreCase))
            ?? throw new StudioXException("TOOLS_MIGRATION_PACK", "缺少原工程的精确器件包，无法判断器件支持文件是否被用户修改；请先导入原包。");
        oldPack = await PackRepository.VerifyAsync(oldPack, token);
        targetPack = await PackRepository.VerifyAsync(targetPack, token);
        if (targetPack.Manifest.Id != project.PackId || targetPack.Manifest.Version == project.PackVersion)
        {
            throw new StudioXException("TOOLS_MIGRATION_PACK", "请选择同一器件包 ID 的不同明确版本。");
        }
        var oldPlan = ProjectService.Plan(oldPack, project.DeviceId, project.TemplateId, project.Name, project.Logic is not null);
        var next = ProjectService.Plan(targetPack, project.DeviceId, project.TemplateId, project.Name, project.Logic is not null);
        EnsureProfile(next.Project);
        var blockers = new List<string>();
        if (project.Espressif is not null)
        {
            try
            {
                EspressifProjectPath.ValidateNewDirectory(destination);
            }
            catch (StudioXException error) { blockers.Add(error.Code + ": " + error.Message); }
        }
        if (project.Espressif != oldPlan.Project.Espressif || project.PinMapping != oldPlan.Project.PinMapping || project.Logic != oldPlan.Project.Logic)
        {
            blockers.Add("原工程框架或映射/逻辑配置与原器件包声明不同，需要先审阅配置。");
        }
        if (project.Espressif?.Framework != next.Project.Espressif?.Framework || project.Espressif?.Target != next.Project.Espressif?.Target
            || (project.PinMapping is null) != (next.Project.PinMapping is null) || project.PinMapping?.TargetDevice != next.Project.PinMapping?.TargetDevice)
        {
            blockers.Add("不能自动改变构建框架、SDK 目标或 AGM 映射模式。");
        }
        if (oldPlan.Device.Architecture != next.Device.Architecture || oldPlan.Device.FlashOrigin != next.Device.FlashOrigin
            || oldPlan.Device.FlashBytes != next.Device.FlashBytes || oldPlan.Device.RamOrigin != next.Device.RamOrigin || oldPlan.Device.RamBytes != next.Device.RamBytes
            || !oldPlan.Device.CpuFlags.SequenceEqual(next.Device.CpuFlags) || oldPlan.Device.OpenOcd?.ApplicationFlashBytes != next.Device.OpenOcd?.ApplicationFlashBytes)
        {
            blockers.Add("目标包的架构、CPU/ABI 参数或内存/应用区布局不同，需要人工审阅，不能自动复制器件支持。");
        }
        var needs = await ProjectDevelopmentComponents.ReadAsync(root, project, token);
        _ = await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token);
        var declared = ProjectDevelopmentComponents.FromSnapshot(oldPlan.Project);
        if (needs.Count != declared.Count || declared.Any(d => !needs.Any(n => DevelopmentComponentRequirements.SameIdentity(d, n))))
        {
            blockers.Add("原工程组件需求与原器件包不一致，需要先审阅原工程配置。");
        }
        await CheckDeviceFilesAsync(root, oldPack, oldPlan, blockers, token);
        var snapshot = await CaptureAsync(root, token);
        if (snapshot.Fingerprint != before.Fingerprint || project != await ProjectService.ReadAsync(root, token))
        {
            throw new StudioXException("TOOLS_PROJECT_CHANGED", "检查期间原工程发生变化，请重新预览。");
        }
        return new ComponentMigrationPreview(root, destination, snapshot.Fingerprint, oldPack, targetPack, project, next.Project,
            snapshot.UserFiles.Count, snapshot.Bytes, blockers);
    }, token);

    public Task<ComponentMigrationResult> CreateAndBuildAsync(ComponentMigrationPreview preview, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("TOOLS_MIGRATION_BUSY", "已有升级副本验证正在进行。");
        }
        var created = false;
        try
        {
            progress?.Report("重新校验原工程、目标器件包与升级预览…");
            var fresh = await PreviewAsync(preview.SourceDirectory, preview.TargetPack, preview.DestinationDirectory, token);
            if (!fresh.CanCreate)
            {
                throw new StudioXException("TOOLS_MIGRATION_REVIEW", fresh.ToText());
            }
            if (fresh.SourceFingerprint != preview.SourceFingerprint || fresh.TargetProject != preview.TargetProject || fresh.TargetPack.ContentHash != preview.TargetPack.ContentHash)
            {
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "原工程或目标器件包在预览后发生变化，请重新预览。");
            }
            var snapshot = await CaptureAsync(fresh.SourceDirectory, token);
            if (snapshot.Fingerprint != fresh.SourceFingerprint)
            {
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "原工程在复制前发生变化。");
            }
            progress?.Report("生成新器件支持并复制用户源码…");
            await new ProjectService().CreateAsync(fresh.TargetPack, fresh.OriginalProject.DeviceId, fresh.OriginalProject.TemplateId,
                fresh.OriginalProject.Name, fresh.DestinationDirectory, token, fresh.OriginalProject.Logic is not null);
            created = true;
            foreach (var (relative, hash) in snapshot.UserFiles)
            {
                token.ThrowIfCancellationRequested();
                var target = PathBoundary.Resolve(fresh.DestinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = new FileStream(PathBoundary.Resolve(fresh.SourceDirectory, relative), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                {
                    await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        await input.CopyToAsync(output, token);
                    }
                }
                if (await HashAsync(target, token) != hash)
                {
                    throw new StudioXException("TOOLS_PROJECT_CHANGED", "复制期间用户文件发生变化：" + relative);
                }
            }
            if ((await CaptureAsync(fresh.SourceDirectory, token)).Fingerprint != fresh.SourceFingerprint)
            {
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "复制期间原工程发生变化，尚未编译副本。");
            }
            if (fresh.OriginalProject.Espressif is not null)
            {
                // Kconfig 可能重写副本配置，保留输入供升级后逐项比较；原工程完全不动。
                foreach (var relative in new[] { "sdkconfig", "sdkconfig.defaults", "dependencies.lock" })
                {
                    var file = PathBoundary.Resolve(fresh.DestinationDirectory, relative);
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    var backup = PathBoundary.Resolve(fresh.DestinationDirectory, ".studiox/migration-inputs/" + relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(file, backup, false);
                }
            }
            progress?.Report("仅编译验证副本，不连接或下载硬件…");
            var report = await builds.BuildAsync(fresh.DestinationDirectory, progress, token);
            var result = new ComponentMigrationResult(fresh.SourceDirectory, fresh.DestinationDirectory, report.Success, false, report.Log, report.LogPath);
            await SaveResultAsync(result);
            return result;
        }
        catch (Exception error) when (created)
        {
            var result = new ComponentMigrationResult(preview.SourceDirectory, preview.DestinationDirectory, false, error is OperationCanceledException,
                error.ToString(), null);
            await SaveResultAsync(result);
            return result;
        }
        finally { gate.Release(); }
    }, token);

    private static Task SaveResultAsync(ComponentMigrationResult result) => JsonStore.WriteAsync(PathBoundary.Resolve(result.DestinationDirectory,
        ".studiox/component-migration-result.json"), result, CancellationToken.None);
    private static void EnsureProfile(ProjectManifest project)
    {
        if (project.Kind != ProjectKind.Pack || project.CubeMx is not null)
        {
            throw new StudioXException("TOOLS_MIGRATION_PROFILE", "副本迁移支持原生 Pack、Espressif SDK 与 AGM 映射/逻辑工程。CubeMX、MicroPython 和 Zephyr 需要各自的迁移策略；请先查看升级差异。");
        }
    }
    private static void ValidateDestination(string source, string destination, string packDirectory)
    {
        static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Contains(source, destination) || Contains(destination, source) || Contains(Path.GetFullPath(packDirectory), destination))
        {
            throw new StudioXException("TOOLS_MIGRATION_PATH", "验证副本须位于原工程和器件包以外的新目录，不能是原工程的父目录。");
        }
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new StudioXException("PROJECT_EXISTS", "验证副本目录已存在，不能覆盖。");
        }
        RejectLinkedAncestors(destination);
        _ = PathBoundary.Resolve(Path.GetDirectoryName(destination)!, Path.GetFileName(destination));
    }
    private static void RejectLinkedAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "迁移路径的父目录不能使用链接或重解析点：" + current);
            }
        }
    }
    private static async Task<string> HashAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}

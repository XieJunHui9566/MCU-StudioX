namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class ComponentMigrationService
{
    private sealed record SourceSnapshot(string Fingerprint, IReadOnlyList<(string Relative, string Hash)> UserFiles, long Bytes);
    private static async Task<SourceSnapshot> CaptureAsync(string root, CancellationToken token)
    {
        var users = new List<(string Relative, string Hash)>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0, userBytes = 0;
        foreach (var path in Files(root, token).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var user = !relative.StartsWith("device/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith(".studiox/", StringComparison.OrdinalIgnoreCase)
                || relative is ProjectBuildSettings.RelativePath or StcIspSettings.RelativePath or StudioX.Engine.Hdl.Ag32NativeBuildSettings.RelativePath
                    or StudioX.Engine.Hdl.HdlSimulationSettings.RelativePath or EspressifModuleSettings.RelativePath or ".studiox/hdl-schematic.json";
            var important = user || relative.StartsWith("device/", StringComparison.OrdinalIgnoreCase)
                || relative is ".studiox/project.json" or ".studiox/toolchain.lock.json" or DevelopmentComponentLock.RelativePath
                    or ".studiox/ag32-mapping-toolchain.lock.json" or ".studiox/ag32-logic-toolchain.lock.json" or ".studiox/ag32-system-files.json";
            if (!important)
            {
                continue;
            }
            var size = new FileInfo(path).Length;
            if (size > 512L * 1024 * 1024 || checked(bytes + size) > 8L * 1024 * 1024 * 1024)
            {
                throw new StudioXException("TOOLS_MIGRATION_SIZE", "迁移输入超出单文件 512 MiB 或总计 8 GiB，请先整理工程。");
            }
            var digest = await HashAsync(path, token);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\t" + digest + "\n"));
            if (user)
            {
                users.Add((relative, digest));
                userBytes += size;
            }
            bytes += size;
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), users, userBytes);
    }
    private static IEnumerable<string> Files(string root, CancellationToken token, bool skipArtifacts = true)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "迁移输入不接受链接：" + directory);
            }
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++count > 200000)
                {
                    throw new StudioXException("TOOLS_MIGRATION_SIZE", "工程条目超过 200,000，请先整理构建缓存。");
                }
                if (skipArtifacts && item is DirectoryInfo child)
                {
                    var relative = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                    // 只跳过明确的工程根缓存及厂商逻辑缓存，用户 src/build 等源码目录仍须保留。
                    if (child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || !relative.Contains('/') &&
                        (child.Name.Equals(".build", StringComparison.OrdinalIgnoreCase) || child.Name.Equals("build", StringComparison.OrdinalIgnoreCase)
                         || child.Name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase))
                        || relative is "logic/db" or "logic/incremental_db" or "logic/output_files" or "logic/alta_db" or "logic/logic_db")
                    {
                        continue;
                    }
                }
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("PATH_LINK", "迁移输入不接受链接：" + item.FullName);
                }
                if (item is DirectoryInfo)
                {
                    pending.Push(item.FullName);
                }
                else
                {
                    yield return item.FullName;
                }
            }
        }
    }
    private static async Task CheckDeviceFilesAsync(string root, InstalledPack oldPack, BuildPlan plan, List<string> blockers, CancellationToken token)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (plan.Project.PinMapping is not null)
        {
            try
            {
                foreach (var relative in await Ag32SystemSupport.CheckMigrationFilesAsync(root, token))
                {
                    expected.Add(relative["device/".Length..]);
                }
            }
            catch (StudioXException error) { blockers.Add(error.Code + ": " + error.Message); }
        }
        var template = plan.Device.Templates.Single(t => t.Id == plan.Project.TemplateId);
        foreach (var file in Files(oldPack.RootDirectory, token, false))
        {
            var relative = Path.GetRelativePath(oldPack.RootDirectory, file).Replace('\\', '/');
            if (template.Build is not null && relative.StartsWith("sdk/", StringComparison.Ordinal) && relative != plan.Device.LinkerScript
                && !plan.Device.Sources.Contains(relative, StringComparer.Ordinal)
                && !plan.Device.IncludeDirectories.Any(include => relative.StartsWith(include.TrimEnd('/') + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            expected.Add(relative);
            var copied = PathBoundary.Resolve(Path.Combine(root, "device"), relative);
            if (!File.Exists(copied) || await HashAsync(file, token) != await HashAsync(copied, token))
            {
                blockers.Add("原器件支持文件已改变或缺失，自动替换会丢失修改：device/" + relative);
            }
        }
        foreach (var (relative, content) in plan.Project.Espressif is not null ? Array.Empty<(string, string)>()
            : new[] { (CMakeGenerator.DeviceListPath, CMakeGenerator.RenderDevice(plan)), (CMakeGenerator.PlatformPath, CMakeGenerator.RenderPlatform(plan)) })
        {
            expected.Add(relative["device/".Length..]);
            var file = PathBoundary.Resolve(root, relative);
            var actual = File.Exists(file) ? await File.ReadAllTextAsync(file, token) : null;
            if (actual != content && !(plan.Project.PinMapping is not null && relative == CMakeGenerator.DeviceListPath
                && actual == Ag32SystemSupport.RenderDeviceForMigration(plan)))
            {
                blockers.Add("器件构建配置与生成基准不同，需要人工审阅：" + relative);
            }
        }
        var deviceRoot = Path.Combine(root, "device");
        if (!Directory.Exists(deviceRoot))
        {
            return;
        }
        foreach (var file in Files(deviceRoot, token, false))
        {
            var relative = Path.GetRelativePath(deviceRoot, file).Replace('\\', '/');
            if (!expected.Contains(relative))
            {
                blockers.Add("器件支持目录含额外用户文件，需要人工迁移：device/" + relative);
            }
        }
    }
}

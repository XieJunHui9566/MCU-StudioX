namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

public sealed partial class Ag32PinMappingBuildService
{
    private void CheckPrivateDirectory()
    {
        for (var directory = new DirectoryInfo(LicenseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("AG32_MAPPING_LICENSE", "本机许可目录不能经过重解析点。");
            }
        }
    }

    internal async Task<string> PreparePrivateRuntimeAsync(ResolvedToolset tools, CancellationToken token)
    {
        CheckPrivateDirectory();
        var licensePath = PathBoundary.Resolve(LicenseDirectory, "license.txt");
        if (!File.Exists(licensePath))
        {
            throw new StudioXException("AG32_MAPPING_LICENSE", "Supra 需要有效厂商许可。请导入本机已有的 license.txt；许可只存储在私有用户目录，不随工程或工具发行。");
        }
        var license = await File.ReadAllBytesAsync(licensePath, token);
        if (license.Length is <= 0 or > 64 * 1024)
        {
            throw new StudioXException("AG32_MAPPING_LICENSE", "本机 Supra 许可文件大小不合法。");
        }
        var relativeRun = "runs/" + Guid.NewGuid().ToString("N");
        var run = PathBoundary.Resolve(LicenseDirectory, relativeRun);
        Directory.CreateDirectory(run);
        try
        {
            // ALTA_HOME 同时决定许可和架构库位置；只在私有临时目录组合已索引资源与用户许可。
            // 不向发行工具树写许可证，也不从本机其他 SDK 回退读取未锁定工具资源。
            foreach (var (relative, expectedHash) in tools.Manifest.Sha256.Where(pair => pair.Key.StartsWith("supra/", StringComparison.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                var source = PathBoundary.Resolve(tools.RootDirectory, relative);
                var target = PathBoundary.Resolve(run, relative["supra/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = File.OpenRead(source))
                {
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await input.CopyToAsync(output, token);
                    }
                }
                await using var copied = File.OpenRead(target);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(copied, token)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("AG32_MAPPING_TOOL_CHANGED", "映射资源在复制时发生变化：" + relative);
                }
            }
            var stagedLicense = PathBoundary.Resolve(run, "license/license.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedLicense)!);
            await File.WriteAllBytesAsync(stagedLicense, license, token);
            return run;
        }
        catch { RemovePrivateRuntime(run); throw; }
    }

    internal void RemovePrivateRuntime(string run)
    {
        var relative = Path.GetRelativePath(LicenseDirectory, Path.GetFullPath(run)).Replace((char)92, '/');
        var checkedRun = PathBoundary.Resolve(LicenseDirectory, relative);
        if (!relative.StartsWith("runs/", StringComparison.Ordinal) || relative.Count(character => character == '/') != 1)
        {
            throw new StudioXException("AG32_MAPPING_LICENSE", "私有许可临时目录不在预期边界内。");
        }
        if (Directory.Exists(checkedRun))
        {
            // 先验证整个目标树没有链接，避免递归清理越过私有目录。
            ToolsetSnapshot.Capture(checkedRun, CancellationToken.None);
            Directory.Delete(checkedRun, true);
        }
    }
}

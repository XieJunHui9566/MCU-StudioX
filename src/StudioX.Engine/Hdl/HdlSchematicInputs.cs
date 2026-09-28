namespace StudioX.Engine.Hdl;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>校验工程预览输入并建立可复核快照，避免综合期间读取正在保存的原文件。</summary>
internal static class HdlSchematicInputs
{
    internal const string SettingsPath = ".studiox/hdl-schematic.json";
    private static readonly string[] InputExtensions = [".v", ".sv", ".vh", ".svh", ".mem", ".hex", ".mif", ".dat"];

    internal static async Task<ProjectManifest> RequireProjectAsync(string root, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        var profile = Ag32DeviceCatalog.Find(project.DeviceId);
        if (project.Kind != ProjectKind.Pack || project.Logic is null || profile?.CanMap != true ||
            project.Logic.TargetDevice != profile.TargetDevice)
        {
            throw new StudioXException("HDL_PROJECT", "逻辑电路预览仅对已适配的 AG32 MCU+FPGA 工程开放。");
        }
        return project;
    }

    internal static void Validate(string root, HdlSchematicSettings settings)
    {
        if (settings.FormatVersion != 1 || settings.Sources is null || settings.Sources.Length == 0 ||
            settings.IncludeDirectories is null || settings.Defines is null || settings.TopModule is null)
        {
            throw new StudioXException("HDL_SETTINGS", "配置格式应为 1，且至少选择一个 Verilog 源文件。");
        }
        if (settings.TopModule.Length != 0 && !Regex.IsMatch(settings.TopModule, @"\A[A-Za-z_][A-Za-z0-9_$]*\z"))
        {
            throw new StudioXException("HDL_TOP", "顶层模块请填写 Verilog 标识符，或留空由 Yosys 检查并选择。");
        }
        foreach (var source in settings.Sources)
        {
            var path = PathBoundary.Resolve(root, source);
            if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".v" or ".sv"))
            {
                throw new StudioXException("HDL_SOURCE", "源文件不存在或不是 .v/.sv：" + source);
            }
        }
        foreach (var directory in settings.IncludeDirectories)
        {
            if (!Directory.Exists(directory == "." ? root : PathBoundary.Resolve(root, directory)))
            {
                throw new StudioXException("HDL_INCLUDE", "包含目录不存在：" + directory);
            }
        }
        foreach (var define in settings.Defines)
        {
            if (define is null || !Regex.IsMatch(define, @"\A[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_'+-]+)?\z"))
            {
                throw new StudioXException("HDL_DEFINE", "宏应填写 NAME 或 NAME=整数/Verilog 数值常量：" + define);
            }
        }
    }

    internal static string[] DiscoverSources(string root, string entry)
    {
        var parent = Path.GetDirectoryName(PathBoundary.Resolve(root, entry))!;
        return EnumerateInputs(parent).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".v" or ".sv")
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<string> EnumerateInputs(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(path).StartsWith('.'))
            {
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var nested in EnumerateInputs(path))
                {
                    yield return nested;
                }
            }
            else if (InputExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    internal static async Task<Dictionary<string, string>> SnapshotAsync(string root, string destination,
        HdlSchematicSettings settings, CancellationToken token)
    {
        var files = CollectFiles(root, settings);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, relative), token);
            ValidateDependencies(relative, bytes);
            var target = PathBoundary.Resolve(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, token);
            hashes.Add(relative, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        // 缺失配置也参与时效判断，避免首次生成后新建配置却继续使用旧图。
        foreach (var relative in new[] { ".studiox/project.json", SettingsPath })
        {
            var path = PathBoundary.Resolve(root, relative);
            hashes.Add(relative, File.Exists(path) ? Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))) : "");
        }
        return hashes;
    }

    private static HashSet<string> CollectFiles(string root, HdlSchematicSettings settings)
    {
        var files = new HashSet<string>(settings.Sources, StringComparer.OrdinalIgnoreCase);
        var directories = settings.Sources.Select(source => Path.GetDirectoryName(PathBoundary.Resolve(root, source))!)
            .Concat(settings.IncludeDirectories.Select(directory => directory == "." ? root : PathBoundary.Resolve(root, directory)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var file in EnumerateInputs(directory))
            {
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
        return files;
    }

    internal static IReadOnlyDictionary<string, string> PrepareIncludes(string sourceDirectory,
        HdlSchematicSettings settings, IEnumerable<string> paths, CancellationToken token)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < settings.IncludeDirectories.Length; index++)
        {
            var directory = settings.IncludeDirectories[index];
            var prefix = directory == "." ? "" : directory + "/";
            var alias = "../includes/" + index;
            var targetDirectory = Path.GetFullPath(Path.Combine(sourceDirectory, alias));
            Directory.CreateDirectory(targetDirectory);
            foreach (var path in paths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                path is not (".studiox/project.json" or SettingsPath)))
            {
                token.ThrowIfCancellationRequested();
                var target = PathBoundary.Resolve(targetDirectory, path[prefix.Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(PathBoundary.Resolve(sourceDirectory, path), target);
            }
            // Yosys 的 -I 参数保留引号；用无空格的相对别名传入，显示时恢复工程路径。
            map.Add(alias, prefix);
        }
        return map;
    }

    internal static string Digest(IReadOnlyDictionary<string, string> hashes, HdlSchematicSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(settings)
            + string.Join("\n", hashes.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key + ":" + item.Value)))));

    internal static async Task<bool> IsCurrentAsync(string root, IReadOnlyDictionary<string, string> hashes,
        HdlSchematicSettings settings, CancellationToken token)
    {
        if (!CollectFiles(root, settings).SetEquals(hashes.Keys.Where(key => key is not (".studiox/project.json" or SettingsPath))))
        {
            return false;
        }
        foreach (var (relative, hash) in hashes)
        {
            var path = PathBoundary.Resolve(root, relative);
            if (hash.Length == 0)
            {
                if (File.Exists(path))
                {
                    return false;
                }
                continue;
            }
            if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))) != hash)
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateDependencies(string relative, byte[] bytes)
    {
        if (Path.GetExtension(relative).ToLowerInvariant() is not (".v" or ".sv" or ".vh" or ".svh"))
        {
            return;
        }
        var text = Encoding.UTF8.GetString(bytes);
        // 注释内的示例不算依赖；这里只约束字面路径，不把 HDL 编译器当成安全沙箱。
        text = Regex.Replace(text, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
        foreach (Match match in Regex.Matches(text, "(?:`include\\s+|\\$readmem[hb]\\s*\\(\\s*)\"([^\"]+)\""))
        {
            var dependency = match.Groups[1].Value.Replace('\\', '/');
            if (Path.IsPathRooted(dependency) || dependency.Contains(':') || dependency.Split('/').Contains(".."))
            {
                throw new StudioXException("HDL_DEPENDENCY", relative + " 的依赖须使用快照内的相对路径；请通过包含目录配置代替绝对路径或 ../：" + dependency);
            }
        }
    }
}

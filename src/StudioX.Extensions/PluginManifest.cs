namespace StudioX.Extensions;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>版本化插件清单；完整文件索引用于完整性校验，不代表作者签名。</summary>
public sealed record PluginManifest(
    int FormatVersion,
    int ApiVersion,
    string Id,
    string Version,
    string DisplayName,
    string EntryAssembly,
    string EntryType,
    string[] Capabilities,
    Dictionary<string, string> Sha256,
    string Kind = "dotnet",
    string? EntryExecutable = null,
    string[]? Arguments = null,
    string[]? HostTools = null,
    string? Description = null)
{
    /// <summary>校验清单、入口及目录中的全部文件；旧 decode API 1 保留其明确契约。</summary>
    public static async Task<PluginManifest> ReadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var info = new FileInfo(fullManifestPath);
        if (!info.Exists || info.Length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PLUGIN_MANIFEST", "插件清单缺失、过大或为链接。");
        }
        var root = info.DirectoryName!;
        RejectLinks(root);
        await using var input = File.OpenRead(fullManifestPath);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken);
        RejectDuplicateProperties(document.RootElement);
        var manifest = document.RootElement.Deserialize<PluginManifest>(JsonStore.Options)
            ?? throw new StudioXException("PLUGIN_MANIFEST", "插件清单为空。");
        if (manifest.FormatVersion != 1 || manifest.ApiVersion is not (1 or 2))
        {
            throw new StudioXException("PLUGIN_API", "插件 API 或清单版本不兼容。");
        }
        PackValidator.Token(manifest.Id);
        PackValidator.Version(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 256 ||
            manifest.Description?.Length > 8192 || manifest.Sha256 is null || manifest.Sha256.Count is 0 or > 4096 ||
            manifest.Capabilities is null || manifest.Capabilities.Length is 0 or > 4 ||
            manifest.Capabilities.Distinct(StringComparer.Ordinal).Count() != manifest.Capabilities.Length)
        {
            throw new StudioXException("PLUGIN_MANIFEST", "插件名称、能力或文件索引无效。");
        }
        var supported = manifest.ApiVersion == 1 ? new[] { "decode" } : ["commands", "panels", "agentTools"];
        if (manifest.Capabilities.Any(capability => !supported.Contains(capability, StringComparer.Ordinal)) ||
            (manifest.ApiVersion == 1 && (manifest.Capabilities is not ["decode"] || manifest.Kind != "dotnet")))
        {
            throw new StudioXException("PLUGIN_MANIFEST", "插件能力与声明的 API 版本不兼容。");
        }
        if (manifest.HostTools is not null && (manifest.HostTools.Length > 256 ||
            manifest.HostTools.Any(tool => string.IsNullOrWhiteSpace(tool) || tool.Length > 256) ||
            manifest.HostTools.Distinct(StringComparer.Ordinal).Count() != manifest.HostTools.Length))
        {
            throw new StudioXException("PLUGIN_MANIFEST", "主机工具声明无效。");
        }
        var indexedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, expected) in manifest.Sha256)
        {
            _ = PathBoundary.Resolve(root, relative);
            if (!indexedPaths.Add(relative) || expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            {
                throw new StudioXException("PLUGIN_HASH", "文件索引存在重复路径或无效 SHA-256。");
            }
        }
        string entryRelative;
        if (manifest.Kind == "dotnet")
        {
            if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 1024 || string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
                !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || manifest.EntryExecutable is not null ||
                manifest.Arguments is { Length: > 0 })
            {
                throw new StudioXException("PLUGIN_ENTRY", ".NET 插件必须声明 DLL 与入口类型，不能声明进程参数。");
            }
            entryRelative = manifest.EntryAssembly;
        }
        else if (manifest.Kind == "process" && manifest.ApiVersion == 2)
        {
            if (string.IsNullOrWhiteSpace(manifest.EntryExecutable) ||
                !manifest.EntryExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("PLUGIN_ENTRY", "进程插件入口必须是插件目录内的固定 EXE。");
            }
            entryRelative = manifest.EntryExecutable;
            var executableName = Path.GetFileNameWithoutExtension(entryRelative);
            if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32" }
                .Contains(executableName, StringComparer.OrdinalIgnoreCase))
            {
                throw new StudioXException("PLUGIN_ENTRY", "进程入口不能使用命令解释器或系统脚本宿主。");
            }
            if (manifest.Arguments is { Length: > 64 })
            {
                throw new StudioXException("PLUGIN_ENTRY", "进程参数过多。");
            }
            foreach (var argument in manifest.Arguments ?? [])
            {
                if (argument is null || argument.Length > 8192 || argument.Any(character => character < 32) || Path.IsPathRooted(argument))
                {
                    throw new StudioXException("PLUGIN_ENTRY", "进程参数包含非法字符或绝对路径。");
                }
                var extension = Path.GetExtension(argument);
                if (argument.Contains('/') || argument.Contains('\\') ||
                    new[] { ".py", ".js", ".ps1", ".bat", ".cmd", ".vbs" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    var argumentPath = PathBoundary.Resolve(root, argument);
                    if (!File.Exists(argumentPath) || !indexedPaths.Contains(argument))
                    {
                        throw new StudioXException("PLUGIN_ENTRY", "脚本或文件参数必须是受索引保护的相对文件路径。");
                    }
                }
            }
        }
        else
        {
            throw new StudioXException("PLUGIN_ENTRY", "未知的插件入口种类。");
        }
        _ = PathBoundary.Resolve(root, entryRelative);
        if (!manifest.Sha256.ContainsKey(entryRelative))
        {
            throw new StudioXException("PLUGIN_ENTRY", "插件入口不在文件索引中。");
        }
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(fullManifestPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(manifest.Sha256.Keys))
        {
            throw new StudioXException("PLUGIN_HASH", "插件目录与文件索引不一致。");
        }
        long totalBytes = 0;
        foreach (var (relative, expected) in manifest.Sha256)
        {
            var path = PathBoundary.Resolve(root, relative);
            var length = new FileInfo(path).Length;
            totalBytes += length;
            if (length > 64L * 1024 * 1024 || totalBytes > 256L * 1024 * 1024)
            {
                throw new StudioXException("PLUGIN_LIMIT", "插件文件或目录超过容量限制。");
            }
            await using var source = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("PLUGIN_HASH", $"插件文件校验失败：{relative}");
            }
        }
        return manifest;
    }

    private static void RejectLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PLUGIN_LINK", $"插件目录不能为链接：{directory}");
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PLUGIN_LINK", $"插件不能包含链接：{path}");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                RejectLinks(path);
            }
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new StudioXException("PLUGIN_MANIFEST", $"清单含重复 JSON 字段：{property.Name}");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>只读查看同次成功构建的原始汇编产物，不重新编译或接触硬件。</summary>
public sealed class BuildAssemblyService(ToolsetCatalog catalog)
{
    public static bool Supports(ProjectManifest? project) => project?.Kind == ProjectKind.Pack &&
        project.ToolsetId is "stc.sdcc" or Mx251Profile.ToolsetId;

    public async Task<BuildAssemblyCatalog> ListAsync(string root, CancellationToken token = default)
    {
        var (receipt, bytes) = await CurrentAsync(root, token);
        if (receipt.AssemblyFiles is not { Length: > 0 })
        {
            throw Invalid("本次构建没有记录汇编文件，请重新编译；旧目录中的文件不会作为当前结果显示。");
        }
        return new(receipt.Project.DeviceId, Hash(bytes), receipt.AssemblyFiles);
    }

    public async Task<BuildAssemblyDocument> ReadAsync(string root, BuildAssemblyCatalog expected, BuildAssemblyFile file, CancellationToken token = default)
    {
        var (receipt, bytes) = await CurrentAsync(root, token);
        if (Hash(bytes) != expected.ReceiptSha256 || receipt.AssemblyFiles?.Contains(file) != true)
        {
            throw Invalid("构建结果已变化，请刷新汇编面板。");
        }
        var path = PathBoundary.Resolve(root, file.RelativePath);
        if (!file.RelativePath.StartsWith(".build/", StringComparison.Ordinal) ||
            Path.GetExtension(path).ToLowerInvariant() is not (".asm" or ".lst" or ".rst"))
        {
            throw Invalid("汇编文件路径不属于工程构建输出。");
        }
        var content = await ReadBoundedAsync(path, token);
        if (Hash(content) != file.Sha256)
        {
            throw Invalid("汇编文件在编译后已被修改，请重新编译。");
        }
        var text = new UTF8Encoding(false, false).GetString(content).TrimStart('\ufeff');
        return new(file, Path.GetExtension(path).Equals(".rst", StringComparison.OrdinalIgnoreCase) ? LinkedListing(text) : text, text);
    }

    private async Task<(BuildReceipt Receipt, byte[] Bytes)> CurrentAsync(string root, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        if (!Supports(project))
        {
            throw Invalid("当前汇编查看支持 SDCC 8 位 8051 与 MX251 的 251 / STC32 工程。");
        }
        var path = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(path))
        {
            throw Invalid("请先成功编译当前工程，再查看汇编。");
        }
        var bytes = await ReadBoundedAsync(path, token);
        var receipt = JsonSerializer.Deserialize<BuildReceipt>(bytes, JsonStore.Options) ?? throw Invalid("构建凭据无效。");
        if (receipt.Project != project || receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
        {
            throw Invalid("源码或构建配置已变化，请重新编译后查看对应汇编。");
        }
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        if (tools.Fingerprint != receipt.ToolFingerprint)
        {
            throw Invalid("编译工具已变化，请重新编译。");
        }
        if (receipt.Images.Length == 0)
        {
            throw Invalid("构建凭据缺少固件。");
        }
        foreach (var image in receipt.Images)
        {
            if (Hash(await ReadBoundedAsync(PathBoundary.Resolve(root, image.RelativePath), token)) != image.Sha256)
            {
                throw Invalid("固件在编译后已变化，请重新编译。");
            }
        }
        return (receipt, bytes);
    }

    internal static async Task<BuildAssemblyFile[]?> CaptureAsync(string root, ProjectManifest project, CancellationToken token)
    {
        if (!Supports(project))
        {
            return null;
        }
        var candidates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string? source)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!relative.StartsWith(".build/", StringComparison.Ordinal))
            {
                throw Invalid("汇编输出越过构建目录。");
            }
            path = PathBoundary.Resolve(root, relative);
            if (File.Exists(path))
            {
                candidates.TryAdd(relative, source);
            }
        }
        if (project.ToolsetId == Mx251Profile.ToolsetId)
        {
            var directory = PathBoundary.Resolve(root, ".build/mx251/latest");
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var snapshot = Path.Combine(directory, "project.snapshot.json");
            if (File.Exists(snapshot))
            {
                using var json = JsonDocument.Parse(await ReadBoundedAsync(snapshot, token));
                if (json.RootElement.TryGetProperty("units", out var units))
                {
                    foreach (var unit in units.EnumerateArray())
                    {
                        sources[unit.GetProperty("name").GetString()!] = unit.GetProperty("file").GetString()!;
                    }
                }
            }
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => Path.GetExtension(file).ToLowerInvariant() is ".asm" or ".rst" or ".lst"))
            {
                Add(file, sources.GetValueOrDefault(Path.GetFileNameWithoutExtension(file)));
            }
        }
        else
        {
            var commands = PathBoundary.Resolve(root, ".build/compile_commands.json");
            using var json = JsonDocument.Parse(await ReadBoundedAsync(commands, token));
            foreach (var command in json.RootElement.EnumerateArray())
            {
                var directory = Path.GetFullPath(command.GetProperty("directory").GetString()!, root);
                var source = Path.GetRelativePath(root, Path.GetFullPath(command.GetProperty("file").GetString()!, directory)).Replace('\\', '/');
                var output = command.TryGetProperty("output", out var item) ? item.GetString() : null;
                if (output is null && command.TryGetProperty("command", out var invocation))
                {
                    var match = Regex.Match(invocation.GetString()!, "(?:^|\\s)-o\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>\\S+))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (match.Success)
                    {
                        output = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
                    }
                }
                if (output is null)
                {
                    continue;
                }
                foreach (var extension in new[] { ".asm", ".rst", ".lst" })
                {
                    Add(Path.ChangeExtension(Path.GetFullPath(output, directory), extension), source);
                }
            }
        }
        if (candidates.Count > 4096)
        {
            throw Invalid("汇编产物超过查看上限。");
        }
        var result = new List<BuildAssemblyFile>();
        foreach (var (relative, source) in candidates.OrderBy(pair => Path.GetExtension(pair.Key) == ".rst" ? 0 : Path.GetExtension(pair.Key) == ".asm" ? 1 : 2).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            result.Add(new(relative, Hash(await ReadBoundedAsync(PathBoundary.Resolve(root, relative), token)), source));
        }
        return result.ToArray();
    }

    private static string LinkedListing(string raw)
    {
        var result = new StringBuilder();
        var pattern = new Regex(@"^\s*(?<address>[0-9A-Fa-f]{4,8})\s+(?<bytes>(?:[0-9A-Fa-f]{2}\s+)+)(?:\[\s*\d+\]\s*)?\d+\s+(?<code>.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var line in raw.Split('\n'))
        {
            var match = pattern.Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                var code = Regex.Replace(match.Groups["code"].Value.Trim(), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                result.Append(match.Groups["address"].Value).Append("  ").Append(match.Groups["bytes"].Value.Trim().PadRight(14)).Append("  ").AppendLine(code);
            }
        }
        return result.Length > 0 ? result.ToString() : raw;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw Invalid("汇编或构建记录过大，超过 16 MiB 查看上限。");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static StudioXException Invalid(string message) => new("BUILD_ASSEMBLY", message);
}

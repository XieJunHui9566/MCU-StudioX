namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public static class StcDebugArtifacts
{
    public static async Task<StcDebugArtifact> ReadAsync(string project, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(receiptPath))
        {
            throw new StudioXException("MON51_SYMBOLS", "当前工程没有有效构建回执；请启用 Mon51 调试构建和标准 CDB，保存源码后重新编译。");
        }
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
        var current = await ProjectService.ReadAsync(root, token);
        if (current.DeviceId != "IAP15F2K61S2" || receipt.Project.DeviceId != current.DeviceId || receipt.Project.ToolsetId != current.ToolsetId || receipt.Project.ToolsetVersion != current.ToolsetVersion || receipt.Project.CompilerId != current.CompilerId || receipt.Project.PackId != current.PackId || receipt.Project.PackVersion != current.PackVersion || receipt.Project.PackContentHash != current.PackContentHash || receipt.Project.TemplateId != current.TemplateId)
        {
            throw new StudioXException("MON51_SYMBOLS", "当前器件、开发环境组件或包身份与已构建的 IAP15F2K61S2 调试目标不一致。");
        }
        if (receipt.Project.ToolsetId != "stc.sdcc" || receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
        {
            throw new StudioXException("MON51_SOURCE_CHANGED", "源码与构建回执不一致，请保存源码并启用 SDCC 调试信息重新编译。");
        }
        var image = receipt.Images.SingleOrDefault(i => i.Format == "ihex")
            ?? throw new StudioXException("MON51_SYMBOLS", "构建回执没有 SDCC Intel HEX 产物。");
        if (image.SymbolsPath is null || image.SymbolsSha256 is null)
        {
            throw new StudioXException("MON51_SYMBOLS", "此构建没有 CDB 符号；在编译设置中启用 SDCC --debug 后重新编译。");
        }
        var imagePath = PathBoundary.Resolve(root, image.RelativePath);
        var symbolsPath = PathBoundary.Resolve(root, image.SymbolsPath);
        var bytes = await File.ReadAllBytesAsync(imagePath, token);
        var symbols = await File.ReadAllBytesAsync(symbolsPath, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != image.Sha256 || Convert.ToHexString(SHA256.HashData(symbols)) != image.SymbolsSha256)
        {
            throw new StudioXException("MON51_ARTIFACT_CHANGED", "HEX 或 CDB 已在构建后改变；请重新编译。");
        }
        var (code, present) = ParseImage(bytes);
        if (receipt.CompiledSources is not { Length: > 0 })
        {
            throw new StudioXException("MON51_SYMBOLS", "旧构建回执缺少 SDCC 来源映射，请重新编译。");
        }
        var sources = receipt.CompiledSources.Select(path => PathBoundary.Resolve(root, path)).ToArray();
        if (sources.Any(path => !File.Exists(path)))
        {
            throw new StudioXException("MON51_SOURCE_CHANGED", "构建来源文件已移除，请重新编译。");
        }
        return new(root, imagePath, symbolsPath, System.Text.Encoding.UTF8.GetString(symbols), code, present, image.Sha256, image.SymbolsSha256, receipt.SourceStamp, sources);
    }

    internal static async Task<string[]> ReadCompiledSourcesAsync(string root, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, ".build/compile_commands.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new StudioXException("MON51_SYMBOLS", "SDCC 编译来源列表必须为数组。");
        }
        var sources = new List<string>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var file = item.GetProperty("file").GetString() ?? throw new StudioXException("MON51_SYMBOLS", "SDCC 编译来源缺少文件名。");
            var directory = item.GetProperty("directory").GetString() ?? root;
            var full = Path.GetFullPath(file, Path.GetFullPath(directory, root));
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            _ = PathBoundary.Resolve(root, relative);
            if (!File.Exists(full))
            {
                throw new StudioXException("MON51_SYMBOLS", "SDCC 编译来源不存在：" + relative);
            }
            sources.Add(relative);
        }
        return sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static (byte[] Code, bool[] Present) ParseImage(byte[] bytes)
    {
        StcIntelHex.Validate(bytes, 0xdbfd);
        var code = new byte[0xdbfd];
        var present = new bool[code.Length];
        foreach (var raw in System.Text.Encoding.ASCII.GetString(bytes).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var record = Convert.FromHexString(line[1..]);
            if (record[3] != 0)
            {
                continue;
            }
            var address = record[1] * 256 + record[2];
            for (var i = 0; i < record[0]; i++)
            {
                code[address + i] = record[4 + i];
                present[address + i] = true;
            }
        }
        return (code, present);
    }
}

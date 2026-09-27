namespace StudioX.Engine.Lvgl;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public static class LvglTargetBuildEvidenceService
{
    public static Task<LvglTargetBuildEvidence> ReadAsync(string project, LvglPreviewConfiguration configuration,
        CancellationToken token = default) => Task.Run(() => ReadCoreAsync(Path.GetFullPath(project), configuration, token), token);

    private static async Task<LvglTargetBuildEvidence> ReadCoreAsync(string root, LvglPreviewConfiguration configuration, CancellationToken token)
    {
        try
        {
            var path = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
            if (!File.Exists(path))
            {
                return Unknown("NotBuilt", "尚无目标固件构建，不能估算这套 UI 的目标 Flash / RAM。");
            }
            var receipt = await JsonStore.ReadAsync<BuildReceipt>(path, token);
            if (receipt.Project != await ProjectService.ReadAsync(root, token))
            {
                return Unknown("Stale", "目标工程配置已变化，请重新编译目标固件。");
            }
            if (receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
            {
                return Unknown("Stale", "目标构建缺少当前源码证据或源码已变化；显示这套 UI 的目标占用前需要重新编译。");
            }
            var databasePath = PathBoundary.Resolve(root, ".build/compile_commands.json");
            if (!File.Exists(databasePath))
            {
                return Unknown("Unknown", "缺少目标编译数据库，无法确认所选 UI 已加入固件。");
            }
            await using var databaseStream = File.OpenRead(databasePath);
            using var database = await JsonDocument.ParseAsync(databaseStream, cancellationToken: token);
            var compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in database.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var source = item.GetProperty("file").GetString()!;
                var directory = item.TryGetProperty("directory", out var value) ? value.GetString() : root;
                compiled.Add(Path.GetFullPath(source, directory ?? root));
            }
            // 不能证明 PC 适配器与目标同源时，保守标记未知；不能沿用演示 ELF。
            var missing = configuration.SourceFiles.Where(source => !compiled.Contains(Path.GetFullPath(source, root))).ToArray();
            if (missing.Length > 0)
            {
                return new("UiNotIncluded", false, "所选 UI / 资源源码没有全部加入目标构建；不能把已有 ELF 占用标为这套 UI 的占用。", missing);
            }
            var images = receipt.Images.Where(image => image.SymbolsPath is not null || image.Format == "elf").ToArray();
            if (images.Length == 0)
            {
                return Unknown("Unknown", "构建凭据没有目标 ELF，无法核对静态占用来源。");
            }
            foreach (var image in images)
            {
                var elf = PathBoundary.Resolve(root, image.SymbolsPath ?? image.RelativePath);
                if (!File.Exists(elf))
                {
                    return Unknown("Stale", "目标 ELF 已缺失，请重新编译。");
                }
                await using var stream = File.OpenRead(elf);
                var expected = image.SymbolsSha256 ?? image.Sha256;
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return Unknown("Stale", "目标 ELF 已变化，与构建凭据不一致。");
                }
                var builtAt = File.GetLastWriteTimeUtc(elf);
                var shared = Path.GetFullPath(configuration.LvglDirectory, root);
                if (HasNewerLibraryInput(shared, builtAt, token))
                {
                    return Unknown("Stale", "共享 LVGL 文件在目标构建后有变化，请重新编译目标固件。");
                }
            }
            return new("SourcesIncluded", true, "所选 UI 源码在当前目标编译数据库中，ELF 与构建凭据一致；占用仍为静态链接统计，外部库只有修改时间核对。", []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or StudioXException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return Unknown("Unknown", "目标 UI 构建证据暂不可核对：" + ex.Message);
        }
    }

    private static LvglTargetBuildEvidence Unknown(string state, string message) => new(state, false, message, []);

    private static bool HasNewerLibraryInput(string root, DateTime builtAt, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("LVGL_LIBRARY_LINK", "共享库包含重解析点，无法核对其目标构建证据。");
                }
                if (entry is DirectoryInfo)
                {
                    if (entry.Name is not (".git" or ".build" or "bin" or "obj"))
                    {
                        pending.Push(entry.FullName);
                    }
                }
                else if ((entry.Extension.ToLowerInvariant() is ".c" or ".h" or ".cpp" or ".hpp") && entry.LastWriteTimeUtc > builtAt)
                {
                    return true;
                }
            }
        }
        return false;
    }
}

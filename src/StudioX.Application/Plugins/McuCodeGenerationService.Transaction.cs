namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Foundation;

public sealed partial class McuCodeGenerationService
{
    public async Task RecoverAsync(string root, CancellationToken token = default)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(root), _ => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(root, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private string JournalDirectory(string root) => Path.Combine(dataDirectory, "mcu-generation-transactions", Hash(Utf8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))!);

    private async Task CommitAsync(string root, GenerationFileChange[] changes, Func<Task<bool>> hasDirtyBuffers, CancellationToken token)
    {
        if (changes.Length == 0)
        {
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        var directory = JournalDirectory(root);
        Directory.CreateDirectory(directory);
        var journal = Path.Combine(directory, id + ".json");
        // 恢复记录先落盘；工程中只保存无绝对路径的操作 ID，所有输出之后才提交配置凭据。
        await File.WriteAllBytesAsync(journal, JsonSerializer.SerializeToUtf8Bytes(changes, JsonStore.Options), token).ConfigureAwait(false);
        var marker = PathBoundary.Resolve(root, Pending);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var markerTemporary = marker + ".tmp-" + id;
        try
        {
            await File.WriteAllBytesAsync(markerTemporary, JsonSerializer.SerializeToUtf8Bytes(new
            {
                operationId = id
            }, JsonStore.Options), token).ConfigureAwait(false);
            File.Move(markerTemporary, marker, overwrite: false);
        }
        finally { if (File.Exists(markerTemporary)) { File.Delete(markerTemporary); } }
        try
        {
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                if (await hasDirtyBuffers().ConfigureAwait(false))
                {
                    throw Invalid("生成期间编辑器出现未保存修改；正在回滚。");
                }
                await WriteAsync(root, change.Path, change.Before, change.After, token).ConfigureAwait(false);
            }
            File.Delete(marker);
        }
        catch (Exception original)
        {
            try
            {
                await RecoverCoreAsync(root, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception recovery) { throw new AggregateException("生成中断且恢复存在冲突；备份与待恢复标记已保留。", original, recovery); }
            throw;
        }
    }

    private async Task RecoverCoreAsync(string root, CancellationToken token)
    {
        var marker = PathBoundary.Resolve(root, Pending);
        if (!File.Exists(marker))
        {
            return;
        }
        if (new FileInfo(marker).Length > 1024)
        {
            throw Invalid("生成恢复标记无效。");
        }
        using var parsed = JsonDocument.Parse(await File.ReadAllBytesAsync(marker, token).ConfigureAwait(false));
        var id = parsed.RootElement.GetProperty("operationId").GetString()!;
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw Invalid("生成恢复操作 ID 无效。");
        }
        var journal = PathBoundary.Resolve(JournalDirectory(root), id + ".json");
        if (!File.Exists(journal) || new FileInfo(journal).Length > 2 * 1024 * 1024)
        {
            throw Invalid("缺少原用户目录的生成备份；请保留工程文件并恢复该用户目录。");
        }
        var changes = JsonSerializer.Deserialize<GenerationFileChange[]>(await File.ReadAllBytesAsync(journal, token).ConfigureAwait(false), JsonStore.Options) ?? throw Invalid("生成恢复记录为空。");
        if (changes.Length is < 1 or > 40 || changes.Any(change => !SafeOutput(change.Path) && change.Path is not ("src/main.c" or "CMakeLists.txt" or "device.mcuconfig")))
        {
            throw Invalid("生成恢复路径无效。");
        }
        // 先复核完整事务；外部修改不能被恢复过程覆盖。
        foreach (var change in changes)
        {
            var disk = await BytesAsync(root, change.Path, token).ConfigureAwait(false);
            if (Hash(disk) != Hash(change.Before) && Hash(disk) != Hash(change.After))
            {
                throw Invalid("恢复文件发生外部修改，备份已保留：" + change.Path);
            }
        }
        foreach (var change in changes.Reverse())
        {
            if (Hash(await BytesAsync(root, change.Path, token).ConfigureAwait(false)) == Hash(change.After))
            {
                await WriteAsync(root, change.Path, change.After, change.Before, token).ConfigureAwait(false);
            }
        }
        File.Delete(marker);
    }

    private static async Task<byte[]?> BytesAsync(string root, string relative, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, relative);
        if (Directory.Exists(path))
        {
            throw Invalid("文件路径被目录占用：" + relative);
        }
        if (!File.Exists(path))
        {
            return null;
        }
        if (new FileInfo(path).Length > 256 * 1024)
        {
            throw Invalid("生成相关文件容量超过 256 KiB：" + relative);
        }
        return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
    }

    private static async Task CheckAsync(string root, GenerationFileChange change, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, change.Path);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ||
            Hash(await BytesAsync(root, change.Path, token).ConfigureAwait(false)) != Hash(change.Before))
        {
            throw Invalid("文件已变化或为只读，未覆盖：" + change.Path);
        }
    }

    private static async Task WriteAsync(string root, string relative, byte[]? before, byte[]? after, CancellationToken token)
    {
        var change = new GenerationFileChange(relative, before, after);
        await CheckAsync(root, change, token).ConfigureAwait(false);
        var path = PathBoundary.Resolve(root, relative);
        if (after is null)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, after, token).ConfigureAwait(false);
            await CheckAsync(root, change, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: before is not null);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

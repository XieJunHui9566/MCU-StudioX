namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>记录先落盘再移动组件；重启后按实际目录状态预览，保留备份并拒绝覆盖未知内容。</summary>
internal static class ToolRepairTransactions
{
    internal static string RecordPath(string root, string key) => PathBoundary.Resolve(root, ".transactions/" + Key(key) + ".json");
    internal static string Stage(string root, string key) => PathBoundary.Resolve(root, ".repair-" + Key(key));
    internal static string Backup(string root, string key) => PathBoundary.Resolve(root, ".rollback-" + Key(key));
    internal static string Target(string root, ToolRepairRecord record) => PathBoundary.Resolve(root, record.Id + "/" + record.Version);
    private static string Key(string key) => Guid.TryParseExact(key, "N", out _) ? key
        : throw new StudioXException("TOOLS_RECOVERY_RECORD", "恢复事务编号无效。");

    internal static async Task WriteAsync(string root, ToolRepairRecord record)
    {
        var path = RecordPath(root, record.TransactionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = PathBoundary.Resolve(root, ".transactions/" + record.TransactionId + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Flush(true) 在任何目录替换之前完成；取消不能留下已移动目录却没有事务记录的窗口。
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonStore.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            if (record.State != "prepared")
            {
                var history = PathBoundary.Resolve(root, ".transactions/history/" + record.TransactionId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(history)!);
                File.Move(path, history, overwrite: false);
            }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    internal static async Task<ToolRepairRecord> ReadAsync(string root, string key, CancellationToken token)
    {
        var path = RecordPath(root, key);
        if (new FileInfo(path).Length > 64 * 1024)
        {
            throw new StudioXException("TOOLS_RECOVERY_RECORD", "恢复记录超出范围。");
        }
        var record = await JsonStore.ReadAsync<ToolRepairRecord>(path, token);
        PackValidator.Token(record.Id);
        PackValidator.Version(record.Version);
        PackValidator.Token(record.CompilerId);
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (record.FormatVersion != 1 || record.TransactionId != key || !Hash(record.Fingerprint) ||
            record.PreviousFingerprint is not null && !Hash(record.PreviousFingerprint) ||
            record.State is not ("prepared" or "committed" or "rolled-back" or "restored"))
        {
            throw new StudioXException("TOOLS_RECOVERY_RECORD", "恢复记录身份或状态无效。");
        }
        _ = Target(root, record);
        _ = Stage(root, key);
        _ = Backup(root, key);
        return record;
    }

    internal static async Task<(IReadOnlyList<ToolRepairRecovery> Items, IReadOnlyList<string> Diagnostics)> InspectAsync(string root, CancellationToken token)
    {
        var folder = PathBoundary.Resolve(root, ".transactions");
        if (!Directory.Exists(folder))
        {
            return ([], []);
        }
        var items = new List<ToolRepairRecovery>();
        var diagnostics = new List<string>();
        var paths = Directory.EnumerateFiles(folder, "*.json").Take(257).ToArray();
        if (paths.Length > 256)
        {
            throw new StudioXException("TOOLS_RECOVERY_RECORD", "恢复记录过多，请先检查组件事务目录。");
        }
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var record = await ReadAsync(root, Path.GetFileNameWithoutExtension(path), token);
                if (record.State == "prepared")
                {
                    items.Add(await PreviewAsync(root, record, token));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { diagnostics.Add(error.ToString()); }
        }
        return (items, diagnostics);
    }

    internal static async Task<ToolRepairRecovery> PreviewAsync(string root, ToolRepairRecord record, CancellationToken token)
    {
        var target = Target(root, record);
        var stage = PathBoundary.Resolve(Stage(root, record.TransactionId), record.Id + "/" + record.Version);
        var backup = PathBoundary.Resolve(Backup(root, record.TransactionId), record.Id + "/" + record.Version);
        var targetHash = await ManifestHashAsync(target, token);
        var stageHash = await ManifestHashAsync(stage, token);
        var backupHash = await ManifestHashAsync(backup, token);
        var action = "blocked";
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            if (stageHash == record.Fingerprint)
            {
                action = "finish";
            }
            else if (record.HadPrevious && backupHash is not null && backupHash == record.PreviousFingerprint)
            {
                action = "restore";
            }
        }
        // 原组件可能正因为缺失清单而需要修复；未移动时允许明确保留损坏原目录并结束记录，不发布未知内容。
        else if (record.HadPrevious && Directory.Exists(target) && !Directory.Exists(backup) && !File.Exists(backup)
            && targetHash == record.PreviousFingerprint)
        {
            action = "keep";
        }
        else if (targetHash == record.Fingerprint)
        {
            action = "confirm";
        }
        var stamp = string.Join('|', new[] { target, stage, backup }.Select(path =>
            path + ":" + (Directory.Exists(path) ? "directory" : File.Exists(path) ? "file" : "absent")))
            + "|" + targetHash + "|" + stageHash + "|" + backupHash;
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(RecordPath(root, record.TransactionId), token)));
        return new ToolRepairRecovery(record.TransactionId, record.Id, record.Version, record.CompilerId, hash, stamp, action,
            $"{record.Id} / {record.Version}\n事务：{record.TransactionId}\n原始诊断：{record.Diagnostic}\n"
            + (action == "blocked" ? "目录与记录不一致，保留全部文件，请检查原始记录或使用明确匹配的离线归档。"
                : action == "keep" ? "原目录仍在。结束记录后请校验现有组件，必要时重新离线修复；保留暂存内容。"
                : "恢复前重新完整校验候选组件；不修改工程、内容锁、启用状态或其他版本，原备份保留。"))
        {
            CanRestorePrevious = !Directory.Exists(target) && !File.Exists(target) && record.HadPrevious && backupHash is not null && backupHash == record.PreviousFingerprint
        };
    }

    internal static async Task<string?> ManifestHashAsync(string folder, CancellationToken token)
    {
        var path = PathBoundary.Resolve(folder, "toolset.json");
        if (!File.Exists(path))
        {
            return null;
        }
        if (new FileInfo(path).Length > ToolchainArchive.MaximumManifestBytes)
        {
            throw new StudioXException("TOOLS_RECOVERY_RECORD", "组件清单过大。");
        }
        return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token))).ToLowerInvariant();
    }
}

namespace StudioX.KeilImporter;

using System.Text.Json;

/// <summary>从已安装格式 1 包读取明确器件，完整核验移植所用文件及元数据。</summary>
public static class DeviceCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<DeviceChoice[]> SearchAsync(string repository, string query, CancellationToken token)
    {
        repository = ImportPaths.Absolute(repository);
        if (!Directory.Exists(repository) || query.Length is < 3 or > 80)
        {
            throw new InvalidOperationException("请检查已安装器件包目录，并输入至少 3 位 STM32 型号关键词。");
        }
        var choices = new List<DeviceChoice>();
        foreach (var id in Directory.EnumerateDirectories(repository).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            ImportPaths.RejectLinks(id);
            if (Path.GetFileName(id).StartsWith('.'))
            {
                continue;
            }
            foreach (var version in Directory.EnumerateDirectories(id).Order(StringComparer.Ordinal))
            {
                ImportPaths.RejectLinks(version);
                var manifest = await ReadJsonAsync(Path.Combine(version, "payload", "manifest.json"), token);
                if (manifest.GetProperty("formatVersion").GetInt32() != 1)
                {
                    throw new InvalidOperationException("只接受 StudioX Pack 格式 1。");
                }
                var identity = manifest.GetProperty("id").GetString()!;
                var number = manifest.GetProperty("version").GetString()!;
                if (identity != Path.GetFileName(id) || number != Path.GetFileName(version))
                {
                    throw new InvalidOperationException("器件包目录与清单身份不一致。");
                }
                var installation = await ReadJsonAsync(Path.Combine(version, "installation.json"), token);
                var hash = installation.GetProperty("contentHash").GetString()!;
                var index = await ReadJsonAsync(Path.Combine(version, "files.sha256.json"), token);
                var expected = index.GetProperty("manifest.json").GetString();
                if (installation.GetProperty("formatVersion").GetInt32() != 1 ||
                    await ImportPaths.HashAsync(Path.Combine(version, "payload", "manifest.json"), token) != expected)
                {
                    throw new InvalidOperationException("器件包清单校验失败，请在 IDE 中重新导入该包。");
                }
                foreach (var device in manifest.GetProperty("devices").EnumerateArray())
                {
                    var deviceId = device.GetProperty("id").GetString()!;
                    if (deviceId.StartsWith("STM32", StringComparison.OrdinalIgnoreCase) &&
                        device.GetProperty("architecture").GetString() == "arm" &&
                        deviceId.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        choices.Add(new(identity, number, hash, version, device.Clone()));
                    }
                }
            }
        }
        if (choices.Count > 80)
        {
            throw new InvalidOperationException("匹配超过 80 项，请输入更完整的型号关键词；不会截断后自动选择。");
        }
        return choices.OrderBy(choice => choice.Id, StringComparer.Ordinal).ThenBy(choice => choice.Key, StringComparer.Ordinal).ToArray();
    }

    public static async Task VerifyAsync(DeviceChoice choice, CancellationToken token)
    {
        var root = ImportPaths.Absolute(Path.Combine(choice.PackDirectory, "payload"));
        var index = await ReadJsonAsync(Path.Combine(choice.PackDirectory, "files.sha256.json"), token);
        var entries = index.EnumerateObject().ToArray();
        if (entries.Length is < 1 or > 10000 || entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
        {
            throw new InvalidOperationException("器件包索引数量无效或有大小写冲突。");
        }
        var canonical = string.Join('\n', entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name + "\t" + entry.Value.GetString()?.ToLowerInvariant()));
        if (ImportPaths.HashText(canonical) != choice.ContentHash)
        {
            throw new InvalidOperationException("器件包索引在选择后变化，请重新搜索型号。");
        }
        long bytes = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var path = ImportPaths.PackPath(root, entry.Name);
            var expected = entry.Value.GetString();
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64 * 1024 * 1024 || expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException("器件包索引文件无效：" + entry.Name);
            }
            bytes = checked(bytes + file.Length);
            if (bytes > 512 * 1024 * 1024 || !string.Equals(await ImportPaths.HashAsync(path, token), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("器件包文件校验失败：" + entry.Name);
            }
        }
        var manifest = await ReadJsonAsync(Path.Combine(root, "manifest.json"), token);
        var current = manifest.GetProperty("devices").EnumerateArray().Single(device => device.GetProperty("id").GetString() == choice.Id);
        if (manifest.GetProperty("id").GetString() != choice.PackId || manifest.GetProperty("version").GetString() != choice.PackVersion ||
            current.GetRawText() != choice.Device.GetRawText())
        {
            throw new InvalidOperationException("所选器件元数据已经变化，请重新选择。");
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(string path, CancellationToken token)
    {
        ImportPaths.RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new InvalidOperationException("元数据缺失或超过 2 MiB：" + path);
        }
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return document.RootElement.Clone();
    }
}

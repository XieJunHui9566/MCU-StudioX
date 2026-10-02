namespace StudioX.Application.Components;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>组件是版本锁定的源码数据；生成受控 CMake，不运行归档中的脚本。</summary>
public sealed class ComponentService(Func<bool> busy, BuildService? builds = null)
{
    private const string LockFile = "studiox-components.lock.json";
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<ComponentArchivePreview> PreviewAsync(string archive, CancellationToken token = default) => Task.Run(async () =>
    {
        var path = Path.GetFullPath(archive);
        using var zip = ZipFile.OpenRead(path);
        var description = zip.GetEntry("component.json") ?? throw new StudioXException("COMPONENT_FORMAT", "缺少 component.json。");
        if (description.Length > 1024 * 1024 || zip.Entries.Count > 50000) { throw new StudioXException("COMPONENT_SIZE", "组件清单或文件数量过大。"); }
        using var reader = new StreamReader(description.Open());
        var manifest = JsonSerializer.Deserialize<ComponentManifest>(await reader.ReadToEndAsync(token), JsonStore.Options)
            ?? throw new StudioXException("COMPONENT_FORMAT", "组件清单为空。");
        PackValidator.Token(manifest.Id);
        PackValidator.Version(manifest.Version);
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.License) || manifest.Sha256 is null || !manifest.Sha256.Keys.Any(k => k.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase))
            || manifest.Sources is null || manifest.IncludeDirectories is null || manifest.Frameworks is null || manifest.Frameworks.Length == 0 || manifest.Frameworks.Any(f => f is not ("cmake" or "esp-idf"))) { throw new StudioXException("COMPONENT_FORMAT", "组件格式、框架、来源或许可证不完整。"); }
        var uri = new Uri(manifest.SourceUrl);
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) { throw new StudioXException("COMPONENT_SOURCE", "组件来源必须是公开 HTTPS URL。"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            CheckPath(entry.FullName);
            if (!names.Add(entry.FullName) || entry.Length > 64 * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) { throw new StudioXException("COMPONENT_ENTRY", "组件含重复、过大或链接条目。"); }
            total = checked(total + entry.Length);
            if (total > 1024 * 1024 * 1024) { throw new StudioXException("COMPONENT_SIZE", "组件展开超过 1 GiB。"); }
            if (entry.FullName == "component.json") { continue; }
            if (!manifest.Sha256.TryGetValue(entry.FullName, out var expected)) { throw new StudioXException("COMPONENT_INDEX", "归档含未索引文件。"); }
            await using var stream = entry.Open();
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(expected, StringComparison.OrdinalIgnoreCase)) { throw new StudioXException("COMPONENT_HASH", "组件文件损坏：" + entry.FullName); }
        }
        if (manifest.Sha256.Count != zip.Entries.Count - 1) { throw new StudioXException("COMPONENT_INDEX", "组件文件集合不一致。"); }
        foreach (var source in manifest.Sources)
        {
            CheckPath(source);
            if (!manifest.Sha256.ContainsKey(source) || Path.GetExtension(source) is not (".c" or ".cpp" or ".cc" or ".s" or ".S")) { throw new StudioXException("COMPONENT_SOURCE", "组件源码未索引或类型不支持。"); }
        }
        foreach (var include in manifest.IncludeDirectories) { CheckPath(include); if (!manifest.Sha256.Keys.Any(k => k.StartsWith(include + "/", StringComparison.Ordinal))) { throw new StudioXException("COMPONENT_INCLUDE", "头文件目录不存在。"); } }
        await using var archiveStream = File.OpenRead(path);
        return new ComponentArchivePreview(path, Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, token)), total, manifest);
    }, token);

    public async Task<ComponentLock?> ReadAsync(string project, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(project, LockFile);
        if (!File.Exists(path)) { return null; }
        RejectLinks(project, path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) { throw new StudioXException("COMPONENT_LOCK", "组件锁定文件过大。"); }
        var result = await JsonStore.ReadAsync<ComponentLock>(path, token);
        if (result.FormatVersion != 1 || result.Components is null || result.History is null || result.GeneratedHashes is null) { throw new StudioXException("COMPONENT_LOCK", "组件锁定格式不支持。"); }
        return result;
    }

    public async Task InstallAsync(string project, ComponentArchivePreview preview, string target, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var maintenance = builds?.AcquireMaintenance(token);
            if (busy()) { throw new StudioXException("COMPONENT_BUSY", "构建或调试期间不能修改组件。"); }
            var root = Path.GetFullPath(project);
            var manifest = await ProjectService.ReadAsync(root, token);
            var framework = manifest.Espressif is { } sdk ? sdk.Framework == "esp-idf" ? "esp-idf" : "unsupported"
                : manifest.Kind is ProjectKind.Pack or ProjectKind.CubeMx ? "cmake" : "unsupported";
            var fresh = await PreviewAsync(preview.Archive, token);
            if (fresh.Sha256 != preview.Sha256 || fresh.Manifest.Id != preview.Manifest.Id || fresh.Manifest.Version != preview.Manifest.Version) { throw new StudioXException("COMPONENT_CHANGED", "组件归档已变化，请重新预览。"); }
            if (!fresh.Manifest.Frameworks.Contains(framework)) { throw new StudioXException("COMPONENT_FRAMEWORK", "组件不支持当前工程框架。"); }
            if (!Regex.IsMatch(target, @"^[a-zA-Z_][a-zA-Z0-9_.-]{0,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { throw new StudioXException("COMPONENT_TARGET", "请输入 CMake 可执行目标名称，例如 firmware。"); }
            var previous = await ReadAsync(root, token);
            if (previous is not null && (previous.Framework != framework || previous.Target != target)) { throw new StudioXException("COMPONENT_TARGET", "已有组件锁定了其他框架或目标。"); }
            var relative = "studiox-components/" + fresh.Manifest.Id + "/" + fresh.Manifest.Version;
            var destination = PathBoundary.Resolve(root, relative);
            RejectLinks(root, destination);
            var installed = new InstalledComponent(fresh.Manifest, fresh.Sha256, relative);
            if (Directory.Exists(destination)) { await ValidateInstalledAsync(root, installed, token); }
            else
            {
                var staging = PathBoundary.Resolve(root, ".studiox/component-staging/" + Guid.NewGuid().ToString("N"));
                RejectLinks(root, staging);
                Directory.CreateDirectory(staging);
                // ZIP 已完整校验，解压仍逐文件用边界解析；生成目录不会执行归档脚本。
                using var zip = ZipFile.OpenRead(fresh.Archive);
                foreach (var entry in zip.Entries)
                {
                    var path = PathBoundary.Resolve(staging, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var input = entry.Open();
                    await input.CopyToAsync(output, token);
                }
                await ValidateInstalledAsync(root, installed with { RelativeDirectory = Path.GetRelativePath(root, staging).Replace('\\', '/') }, token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(staging, destination);
            }
            var entries = (previous?.Components ?? []).Where(c => c.Manifest.Id != installed.Manifest.Id).Append(installed).OrderBy(c => c.Manifest.Id).ToArray();
            await ApplyAsync(root, framework, target, entries, previous, token);
        }
        finally { gate.Release(); }
    }

    public async Task RollbackAsync(string project, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var maintenance = builds?.AcquireMaintenance(token);
            if (busy()) { throw new StudioXException("COMPONENT_BUSY", "构建或调试期间不能回退组件。"); }
            var previous = await ReadAsync(project, token) ?? throw new StudioXException("COMPONENT_HISTORY", "没有组件记录。");
            var revision = previous.History.LastOrDefault() ?? throw new StudioXException("COMPONENT_HISTORY", "没有可恢复的上一版。");
            foreach (var component in revision.Components) { await ValidateInstalledAsync(project, component, token); }
            await ApplyAsync(project, previous.Framework, previous.Target, revision.Components, previous with { History = previous.History[..^1] }, token, false);
        }
        finally { gate.Release(); }
    }

    private static async Task ApplyAsync(string root, string framework, string target, InstalledComponent[] entries, ComponentLock? previous, CancellationToken token, bool remember = true)
    {
        if (framework is not ("cmake" or "esp-idf") || !Regex.IsMatch(target, @"^[a-zA-Z_][a-zA-Z0-9_.-]{0,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { throw new StudioXException("COMPONENT_LOCK", "锁定的框架或目标无效。"); }
        var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var expectedContents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (previous is not null)
        {
            foreach (var (relative, hash) in previous.GeneratedHashes)
            {
                if (relative != "studiox-components.cmake" && !Regex.IsMatch(relative, @"^components/studiox_[a-zA-Z0-9_-]+/CMakeLists\.txt$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) { throw new StudioXException("COMPONENT_LOCK", "锁定文件引用了非托管路径。"); }
                var path = PathBoundary.Resolve(root, relative);
                RejectLinks(root, path);
                if (!File.Exists(path)) { throw new StudioXException("COMPONENT_EDITED", "生成的组件 CMake 不存在：" + relative); }
                var existing = await File.ReadAllBytesAsync(path, token);
                if (Hash(existing) != hash) { throw new StudioXException("COMPONENT_EDITED", "生成的组件 CMake 已被修改，拒绝覆盖：" + relative); }
                expectedContents[relative] = existing;
                writes[relative] = Encoding.UTF8.GetBytes("# 此组件已从当前锁定版本撤销\n");
            }
        }
        if (framework == "cmake")
        {
            var generated = new StringBuilder("# StudioX 管理的版本锁定源码；通过组件管理页更新。\n");
            foreach (var entry in entries)
            {
                foreach (var source in entry.Manifest.Sources) { generated.AppendLine($"target_sources({target} PRIVATE \"${{CMAKE_CURRENT_LIST_DIR}}/{entry.RelativeDirectory}/{source}\")"); }
                foreach (var include in entry.Manifest.IncludeDirectories) { generated.AppendLine($"target_include_directories({target} PRIVATE \"${{CMAKE_CURRENT_LIST_DIR}}/{entry.RelativeDirectory}/{include}\")"); }
            }
            writes["studiox-components.cmake"] = Encoding.UTF8.GetBytes(generated.ToString());
            var cmake = PathBoundary.Resolve(root, "CMakeLists.txt");
            RejectLinks(root, cmake);
            var original = await File.ReadAllBytesAsync(cmake, token);
            expectedContents["CMakeLists.txt"] = original;
            var text = new UTF8Encoding(false, true).GetString(original);
            const string includeLine = "include(\"${CMAKE_CURRENT_LIST_DIR}/studiox-components.cmake\")";
            if (!text.Contains(includeLine, StringComparison.Ordinal)) { writes["CMakeLists.txt"] = original.Concat(Encoding.UTF8.GetBytes("\n# StudioX 版本锁定组件\n" + includeLine + "\n")).ToArray(); }
        }
        else
        {
            foreach (var entry in entries)
            {
                if (entries.Count(c => c.Manifest.Id.Replace('.', '_') == entry.Manifest.Id.Replace('.', '_')) != 1) { throw new StudioXException("COMPONENT_ID_COLLISION", "组件 ID 转换为 ESP-IDF 名称后冲突。"); }
                var content = "# StudioX 管理的组件；启用 MINIMAL_BUILD 时请在 main REQUIRES 中声明本组件。\n";
                content += "idf_component_register(SRCS " + string.Join(" ", entry.Manifest.Sources.Select(s => $"\"${{CMAKE_CURRENT_LIST_DIR}}/../../{entry.RelativeDirectory}/{s}\""));
                content += " INCLUDE_DIRS " + string.Join(" ", entry.Manifest.IncludeDirectories.Select(s => $"\"${{CMAKE_CURRENT_LIST_DIR}}/../../{entry.RelativeDirectory}/{s}\"")) + ")\n";
                writes["components/studiox_" + entry.Manifest.Id.Replace('.', '_') + "/CMakeLists.txt"] = Encoding.UTF8.GetBytes(content);
            }
        }
        foreach (var relative in writes.Keys.Where(p => p != "CMakeLists.txt"))
        {
            var path = PathBoundary.Resolve(root, relative);
            RejectLinks(root, path);
            if (File.Exists(path) && previous?.GeneratedHashes.ContainsKey(relative) != true) { throw new StudioXException("COMPONENT_COLLISION", "已有同名非托管文件：" + relative); }
        }
        var hashes = writes.Where(p => p.Key != "CMakeLists.txt").ToDictionary(p => p.Key, p => Hash(p.Value));
        var history = previous?.History ?? [];
        if (remember && previous is not null) { history = history.Append(new ComponentRevision(DateTimeOffset.UtcNow, previous.Components)).TakeLast(10).ToArray(); }
        writes[LockFile] = JsonSerializer.SerializeToUtf8Bytes(new ComponentLock(1, framework, target, entries, hashes, history), JsonStore.Options);
        var originals = new Dictionary<string, byte[]?>();
        var written = new List<string>();
        try
        {
            foreach (var (relative, bytes) in writes)
            {
                token.ThrowIfCancellationRequested();
                var path = PathBoundary.Resolve(root, relative);
                RejectLinks(root, path);
                originals[relative] = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
                if (expectedContents.TryGetValue(relative, out var expected) && (originals[relative] is not { } present || Hash(present) != Hash(expected))) { throw new StudioXException("COMPONENT_EDITED", "组件预览后文件已被外部修改：" + relative); }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await WriteAtomicAsync(path, bytes, token);
                written.Add(relative);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var relative in written.AsEnumerable().Reverse())
            {
                try
                {
                    var path = PathBoundary.Resolve(root, relative);
                    if (Hash(await File.ReadAllBytesAsync(path, CancellationToken.None)) != Hash(writes[relative])) { throw new IOException("文件被外部修改，未覆盖回退：" + path); }
                    if (originals[relative] is { } bytes) { await WriteAtomicAsync(path, bytes, CancellationToken.None); }
                    else { File.Delete(path); }
                }
                catch (Exception recovery) { errors.Add(recovery); }
            }
            throw new AggregateException("组件事务失败，原始诊断和回退结果：", errors);
        }
    }

    private static async Task ValidateInstalledAsync(string root, InstalledComponent entry, CancellationToken token)
    {
        PackValidator.Token(entry.Manifest.Id);
        PackValidator.Version(entry.Manifest.Version);
        CheckPath(entry.RelativeDirectory);
        foreach (var source in entry.Manifest.Sources) { CheckPath(source); if (!entry.Manifest.Sha256.ContainsKey(source)) { throw new StudioXException("COMPONENT_INDEX", "锁定源码未索引。"); } }
        foreach (var include in entry.Manifest.IncludeDirectories) { CheckPath(include); }
        var directory = PathBoundary.Resolve(root, entry.RelativeDirectory);
        RejectLinks(root, directory);
        var actual = new List<string>();
        void Scan(string folder)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(folder))
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) { throw new StudioXException("COMPONENT_LINK", "组件目录不接受链接。"); }
                if (Directory.Exists(item)) { Scan(item); }
                else { actual.Add(Path.GetRelativePath(directory, item).Replace('\\', '/')); }
            }
        }
        Scan(directory);
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(entry.Manifest.Sha256.Keys.Append("component.json").Order(StringComparer.Ordinal))) { throw new StudioXException("COMPONENT_INDEX", "已安装组件的文件集合发生变化。"); }
        foreach (var (relative, expected) in entry.Manifest.Sha256)
        {
            var path = PathBoundary.Resolve(directory, relative);
            RejectLinks(root, path);
            await using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase)) { throw new StudioXException("COMPONENT_HASH", "已安装组件已修改：" + relative); }
        }
        var description = await JsonStore.ReadAsync<ComponentManifest>(PathBoundary.Resolve(directory, "component.json"), token);
        if (description.Id != entry.Manifest.Id || description.Version != entry.Manifest.Version || JsonSerializer.Serialize(description, JsonStore.Options) != JsonSerializer.Serialize(entry.Manifest, JsonStore.Options)) { throw new StudioXException("COMPONENT_CHANGED", "已安装组件清单与锁定版本不同。"); }
    }
    private static void CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !Regex.IsMatch(path, @"^[a-zA-Z0-9_.\-/]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || path.EndsWith('/') || path.Split('/').Any(p => p is "" or "." or "..")) { throw new StudioXException("COMPONENT_PATH", "组件路径无效。"); }
    }
    private static void RejectLinks(string root, string path)
    {
        var boundary = Path.GetFullPath(root);
        for (var current = Path.GetFullPath(path); current.Length >= boundary.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new StudioXException("COMPONENT_LINK", "组件目录不接受链接。"); }
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) { break; }
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, token); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) { File.Delete(temp); } }
    }
}

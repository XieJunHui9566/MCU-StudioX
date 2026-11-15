namespace StudioX.Engine;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>只复用本进程内成功完成的构建；配置仍先运行，生成输入和产物内容都参与比较。</summary>
internal static class Mx251BuildCache
{
    private sealed record Entry(string Inputs, IReadOnlyDictionary<string, string> Outputs);
    private static readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    internal static async Task<string> InputsAsync(string root, ProjectManifest project, string sourceStamp,
        ResolvedToolset tools, ResolvedToolset buildTools, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        Text(sourceStamp);
        Text(JsonSerializer.Serialize(project, JsonStore.Options));
        Text(tools.RootDirectory);
        Text(tools.Fingerprint);
        Text(buildTools.RootDirectory);
        Text(buildTools.Fingerprint);
        // CMake 每次先配置，用户选项、生成的 SDK 源码以及构建命令变化均使缓存失效。
        var inputs = PathBoundary.Resolve(root, ".build/mx251-cmake/mx251-inputs.json");
        using var specification = JsonDocument.Parse(await File.ReadAllTextAsync(inputs, token));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { inputs,
            PathBoundary.Resolve(root, ".build/mx251-cmake/build.ninja"),
            PathBoundary.Resolve(root, ".build/mx251-cmake/CMakeCache.txt") };
        foreach (var source in specification.RootElement.GetProperty("sources").EnumerateArray())
        {
            paths.Add(PathBoundary.Resolve(root, Path.GetRelativePath(root, source.GetProperty("file").GetString()!).Replace('\\', '/')));
        }
        foreach (var include in specification.RootElement.GetProperty("includeDirectories").EnumerateArray())
        {
            var directory = PathBoundary.Resolve(root, Path.GetRelativePath(root, include.GetString()!).Replace('\\', '/'));
            var snapshot = ToolsetSnapshot.Capture(directory, token);
            foreach (var file in snapshot.Files)
            {
                paths.Add(PathBoundary.Resolve(directory, file));
            }
        }
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            Text(path);
            await using var stream = File.OpenRead(path);
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        // 自定义 CMake/工具读取的宿主环境发生变化时，不沿用上次输出。
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal))
        {
            Text(key);
            Text(Environment.GetEnvironmentVariable(key) ?? "");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static async Task<bool> TryReuseAsync(string root, string inputs, CancellationToken token)
    {
        if (!entries.TryGetValue(root, out var entry) || entry.Inputs != inputs)
        {
            return false;
        }
        var directory = PathBoundary.Resolve(root, ".build/mx251/latest");
        if (!Directory.Exists(directory))
        {
            return false;
        }
        var current = ToolsetSnapshot.Capture(directory, token).Files.ToHashSet(StringComparer.Ordinal);
        if (!current.SetEquals(entry.Outputs.Keys))
        {
            return false;
        }
        foreach (var (file, expected) in entry.Outputs)
        {
            await using var stream = File.OpenRead(PathBoundary.Resolve(directory, file));
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != expected)
            {
                return false;
            }
        }
        return true;
    }

    internal static async Task RememberAsync(string root, string inputs, CancellationToken token)
    {
        var directory = PathBoundary.Resolve(root, ".build/mx251/latest");
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ToolsetSnapshot.Capture(directory, token).Files)
        {
            await using var stream = File.OpenRead(PathBoundary.Resolve(directory, file));
            outputs.Add(file, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
        }
        entries[root] = new(inputs, outputs);
    }

    internal static void Forget(string root) => entries.TryRemove(root, out _);
}

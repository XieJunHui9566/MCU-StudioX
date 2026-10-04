namespace StudioX.PluginRuntimeValidation;

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>仅从本机已有 Python 构造隔离发行夹具，验证真实语言进程的双向协议。</summary>
internal static class PythonRuntimeChecks
{
    public static async Task<string> RunAsync(string repository, string scratch, string hostExecutable)
    {
        var configured = Environment.GetEnvironmentVariable("STUDIOX_PLUGIN_TEST_PYTHON");
        var executable = !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured) :
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(directory => !directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                .Select(directory => Path.Combine(directory.Trim('"'), "python.exe")).FirstOrDefault(File.Exists);
        if (executable is null || !File.Exists(executable))
        {
            throw new InvalidOperationException("真实 Python 验证需要本机已有 python.exe；可通过 STUDIOX_PLUGIN_TEST_PYTHON 显式指定，不下载运行时。");
        }
        var home = Path.GetDirectoryName(executable)!;
        var library = Path.Combine(home, "Lib");
        if (!Directory.Exists(library))
        {
            throw new InvalidOperationException("Python 验证需要包含标准库 Lib 的本机发行目录。");
        }
        var directory = Path.Combine(scratch, "python");
        Directory.CreateDirectory(directory);
        File.Copy(executable, Path.Combine(directory, "python.exe"));
        foreach (var source in Directory.GetFiles(home, "*.dll"))
        {
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        }
        var runtimeDll = Directory.GetFiles(home, "python*.dll")
            .Select(Path.GetFileNameWithoutExtension).First(name => name is not null && name.Length > "python3".Length)!;
        await using (var zipStream = File.Create(Path.Combine(directory, runtimeDll + ".zip")))
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var source in Directory.EnumerateFiles(library, "*.py", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(library, source).Replace('\\', '/');
                    var first = relative.Split('/')[0];
                    if (new[] { "site-packages", "test", "idlelib", "tkinter", "turtledemo", "ensurepip", "__pycache__" }.Contains(first, StringComparer.Ordinal))
                    {
                        continue;
                    }
                    archive.CreateEntryFromFile(source, relative, CompressionLevel.Fastest);
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, runtimeDll + "._pth"), runtimeDll + ".zip\n.\n");
        File.Copy(Path.Combine(repository, "examples/plugins/python/plugin.py"), Path.Combine(directory, "plugin.py"));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sourcePath in Directory.GetFiles(directory))
        {
            await using var source = File.OpenRead(sourcePath);
            hashes[Path.GetFileName(sourcePath)] = Convert.ToHexString(await SHA256.HashDataAsync(source));
        }
        var manifest = new PluginManifest(1, 2, "validation.python", "1.0.0", "Python Validation", "", "",
            ["commands", "panels", "agentTools"], hashes, "process", "python.exe", ["-I", "plugin.py"], ["project_info"]);
        var manifestPath = Path.Combine(directory, "plugin.json");
        await JsonStore.WriteAsync(manifestPath, manifest);
        var events = new ConcurrentQueue<PluginRuntimeEvent>();
        await using var client = await PluginRuntimeClient.StartAsync(hostExecutable, manifestPath, (tool, _, _) =>
        {
            if (tool != "project_info")
            {
                throw new InvalidOperationException("Python 请求了未声明工具。");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                name = "isolated-python-project"
            }));
        }, (update, _) =>
        {
            events.Enqueue(update);
            return Task.CompletedTask;
        });
        var empty = JsonSerializer.SerializeToElement(new
        {
        });
        var callback = await client.InvokeAsync("agentTool", "workspace", empty);
        if (callback.GetProperty("name").GetString() != "isolated-python-project")
        {
            throw new InvalidOperationException("Python nested hostCall 未返回真实响应。");
        }
        var increment = await client.InvokeAsync("command", "increment", empty);
        if (increment.GetProperty("count").GetInt32() != 1)
        {
            throw new InvalidOperationException("Python 持久状态无效。");
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            _ = await client.InvokeAsync("command", "delay", empty, cancellation.Token);
            throw new InvalidOperationException("Python 等待没有取消。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 夹具显式检查调用者取消，之后继续调用以验证子进程生命周期未被混淆。
        }
        var afterCancel = await client.InvokeAsync("command", "increment", empty);
        if (afterCancel.GetProperty("count").GetInt32() != 2 || !events.Any(update => update.Kind == "panel") ||
            !events.Any(update => update.Kind == "log"))
        {
            throw new InvalidOperationException("Python 取消恢复或事件验证失败。");
        }
        return "python: installed local stdlib runtime, nested hostCall, persistent state, panel/log and cooperative cancellation passed";
    }
}

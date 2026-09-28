namespace StudioX.Extensions;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>持久插件进程的应用侧所有者；进程隔离不构成操作系统权限沙箱。</summary>
public sealed class PluginRuntimeClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly PluginProtocolConnection connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim invocationGate = new(1, 1);
    private readonly AsyncLocal<bool> inHostCall = new();
    private readonly StringBuilder diagnosticBuffer = new();
    private readonly object diagnosticsGate = new();
    private readonly Task stderrReader;
    private int disposed;
    private volatile bool hostCallsAllowed;

    private PluginRuntimeClient(
        Process process,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callHost,
        Func<PluginRuntimeEvent, CancellationToken, Task> onEvent)
    {
        this.process = process;
        connection = new PluginProtocolConnection(process.StandardOutput, process.StandardInput, async (method, payload, token) =>
        {
            if (method != "hostCall" || !hostCallsAllowed || inHostCall.Value)
            {
                throw new StudioXException("PLUGIN_HOST_CALL", "插件尚未通过声明校验，或主机调用发生重入。");
            }
            var tool = payload.GetProperty("tool").GetString();
            if (string.IsNullOrWhiteSpace(tool))
            {
                throw new StudioXException("PLUGIN_HOST_CALL", "插件主机工具名称为空。");
            }
            inHostCall.Value = true;
            try
            {
                return await callHost(tool, payload.GetProperty("arguments"), token).ConfigureAwait(false);
            }
            finally
            {
                inHostCall.Value = false;
            }
        }, async (kind, payload, token) =>
        {
            if (kind is not ("panel" or "log"))
            {
                throw new StudioXException("PLUGIN_EVENT", "未知插件事件。");
            }
            await onEvent(new PluginRuntimeEvent(kind, payload), token).ConfigureAwait(false);
        });
        stderrReader = Task.Run(ReadStandardErrorAsync);
    }

    /// <summary>经过校验且已激活的插件贡献。</summary>
    public PluginContribution Contribution { get; private set; } = new([], [], []);

    /// <summary>保留最近 64 KiB 字符的宿主标准错误；不会混入协议标准输出。</summary>
    public string Diagnostics
    {
        get
        {
            lock (diagnosticsGate)
            {
                return diagnosticBuffer.ToString();
            }
        }
    }

    public Task Completion => connection.Completion;

    public bool IsRunning => disposed == 0 && !process.HasExited && !connection.Completion.IsCompleted;

    /// <summary>校验目录后启动、获取贡献并激活；API 1 解码继续使用 PluginClient。</summary>
    public static Task<PluginRuntimeClient> StartAsync(
        string hostExecutable,
        string manifestPath,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callHost,
        Func<PluginRuntimeEvent, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        return StartAsync(hostExecutable, manifestPath, callHost, onEvent, _ => { }, cancellationToken);
    }

    /// <summary>外部贡献校验在激活及授予主机调用之前执行；任何失败终止并清理进程。</summary>
    public static async Task<PluginRuntimeClient> StartAsync(
        string hostExecutable,
        string manifestPath,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callHost,
        Func<PluginRuntimeEvent, CancellationToken, Task> onEvent,
        Action<PluginContribution> validateContribution,
        CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifest = await PluginManifest.ReadAsync(fullManifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest.ApiVersion != 2)
        {
            throw new StudioXException("PLUGIN_API", "通用插件会话需要 API 2；API 1 不自动迁移。");
        }
        var root = Path.GetDirectoryName(fullManifestPath)!;
        var executable = manifest.Kind == "process" ? PathBoundary.Resolve(root, manifest.EntryExecutable!) : Path.GetFullPath(hostExecutable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in manifest.Kind == "process" ? manifest.Arguments ?? [] : ["--extension", fullManifestPath])
        {
            start.ArgumentList.Add(argument);
        }
        // 不继承 IDE 的密钥、令牌、代理或用户配置；这仍不限制插件自行读取当前用户文件。
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                start.Environment[name] = value;
            }
        }
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (systemRoot is not null)
        {
            start.Environment["PATH"] = Path.Combine(systemRoot, "System32");
        }
        var child = new Process { StartInfo = start };
        PluginRuntimeClient? client = null;
        try
        {
            if (!child.Start())
            {
                throw new StudioXException("PLUGIN_START", "无法启动插件宿主。");
            }
            client = new PluginRuntimeClient(child, callHost, onEvent);
            var described = await client.connection.RequestAsync("describe", JsonSerializer.SerializeToElement(new { }),
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            client.Contribution = described.Deserialize<PluginContribution>(JsonStore.Options)
                ?? throw new StudioXException("PLUGIN_CONTRIBUTION", "插件未提供贡献。");
            ValidateBasicContribution(client.Contribution, manifest);
            validateContribution(client.Contribution);
            client.hostCallsAllowed = true;
            _ = await client.connection.RequestAsync("activate", JsonSerializer.SerializeToElement(new { }),
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            using var eventDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            eventDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await client.connection.DrainHandlersAsync(eventDeadline.Token).ConfigureAwait(false);
            return client;
        }
        catch (Exception exception)
        {
            if (client is not null)
            {
                var diagnostics = client.Diagnostics;
                await client.DisposeAsync().ConfigureAwait(false);
                if (exception is not OperationCanceledException && diagnostics.Length > 0)
                {
                    throw new StudioXException("PLUGIN_START", $"{exception}\n宿主标准错误：\n{diagnostics}", exception);
                }
            }
            else
            {
                child.Dispose();
            }
            throw;
        }
    }

    /// <summary>调用插件贡献；同一实例串行处理，主机回调不能重新进入此实例。</summary>
    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (inHostCall.Value)
        {
            throw new StudioXException("PLUGIN_REENTRANT", "主机工具回调不能重新调用当前插件。");
        }
        if (kind is not ("command" or "agentTool") || string.IsNullOrWhiteSpace(id) || id.Length > 256)
        {
            throw new StudioXException("PLUGIN_INVOKE", "插件调用种类或 ID 无效。");
        }
        await invocationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            // 完整构建可能包含多个五分钟工具阶段；插件 RPC 不应先于应用工具截断它。
            return await connection.RequestAsync("invoke", JsonSerializer.SerializeToElement(new { kind, id, arguments }),
                TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            invocationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        hostCallsAllowed = false;
        try
        {
            if (!connection.Completion.IsCompleted && !process.HasExited)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _ = await connection.RequestAsync("deactivate", JsonSerializer.SerializeToElement(new { }),
                    TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or StudioXException or OperationCanceledException or ObjectDisposedException)
        {
            AppendDiagnostic(exception.ToString());
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        KillProcessTree();
        await connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await stderrReader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            AppendDiagnostic(exception.ToString());
        }
        process.Dispose();
    }

    private static void ValidateBasicContribution(PluginContribution contribution, PluginManifest manifest)
    {
        if (contribution.Commands is null || contribution.Panels is null || contribution.AgentTools is null ||
            contribution.Commands.Length > 256 || contribution.Panels.Length > 64 || contribution.AgentTools.Length > 256 ||
            (contribution.Commands.Length > 0 && !manifest.Capabilities.Contains("commands", StringComparer.Ordinal)) ||
            (contribution.Panels.Length > 0 && !manifest.Capabilities.Contains("panels", StringComparer.Ordinal)) ||
            (contribution.AgentTools.Length > 0 && !manifest.Capabilities.Contains("agentTools", StringComparer.Ordinal)))
        {
            throw new StudioXException("PLUGIN_CONTRIBUTION", "插件贡献为空、过多或超出清单能力。");
        }
        foreach (var ids in new[]
        {
            contribution.Commands.Select(command => command?.Id).ToArray(),
            contribution.Panels.Select(panel => panel?.Id).ToArray(),
            contribution.AgentTools.Select(tool => tool?.Id).ToArray()
        })
        {
            if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                throw new StudioXException("PLUGIN_CONTRIBUTION", "插件贡献 ID 为空、重复或过长。");
            }
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var count = await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }
                AppendDiagnostic(new string(buffer, 0, count));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 进程所有者主动结束会话，不将清理取消写作宿主故障。
        }
    }

    private void AppendDiagnostic(string text)
    {
        lock (diagnosticsGate)
        {
            diagnosticBuffer.Append(text);
            if (diagnosticBuffer.Length > 65536)
            {
                diagnosticBuffer.Remove(0, diagnosticBuffer.Length - 65536);
            }
        }
    }

    private void KillProcessTree()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException exception)
        {
            // 对端可在关闭检查与 Kill 之间退出；保留诊断但继续释放本机资源。
            AppendDiagnostic(exception.ToString());
        }
    }
}

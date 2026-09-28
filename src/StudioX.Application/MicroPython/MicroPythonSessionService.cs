namespace StudioX.Application.MicroPython;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>MicroPython 会话的唯一发送者；所有命令串行化，工程切换先取消并释放设备。</summary>
public sealed class MicroPythonSessionService(DeviceHub hub, Func<SerialSettings, IDeviceTransport>? transportFactory = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private CancellationTokenSource? lifetime;
    private DeviceSession? session;
    private FrameSubscription? subscription;
    private RawReplClient? client;
    private string? projectDirectory;
    private MicroPythonProfile? profile;
    private bool disposed;
    public bool IsConnected
    {
        get; private set;
    }
    public string Identity { get; private set; } = "未连接";
    public event Action<string>? Diagnostic;

    public async Task ConnectAsync(string directory, string port, CancellationToken token = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var project = await ProjectService.ReadAsync(directory, token).ConfigureAwait(false);
            if (project.Kind != ProjectKind.MicroPython || project.MicroPython is not { } selected)
            {
                throw new StudioXException("MICROPYTHON_PROJECT", "请先打开 MicroPython 工程。");
            }
            var settings = new SerialSettings(port, Dtr: true);
            settings.Validate();
            lock (sync)
            {
                lifetime = new();
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var transport = transportFactory?.Invoke(settings) ?? new SerialTransport(settings);
            session = await hub.OpenAsync(transport, deadline.Token).ConfigureAwait(false);
            subscription = session.Subscribe(1024);
            client = new(session, subscription);
            await client.EnterAsync(deadline.Token).ConfigureAwait(false);
            var identification = await client.ExecuteAsync("import sys, json\nprint(json.dumps([sys.implementation.name, list(sys.implementation.version[:3]), sys.implementation._machine]))", deadline.Token).ConfigureAwait(false);
            RequireSuccess(identification);
            using var json = JsonDocument.Parse(identification.Output.Trim());
            var info = json.RootElement;
            var version = string.Join('.', info[1].EnumerateArray().Select(item => item.GetInt32()));
            if (info[0].GetString() != "micropython" || version != selected.Version || info[2].GetString() != selected.ExpectedMachine)
            {
                throw new StudioXException("MICROPYTHON_IDENTITY", "解释器报告的板型或版本不匹配：" + identification.Output.Trim());
            }
            profile = selected;
            projectDirectory = Path.GetFullPath(directory);
            Identity = info[2].GetString() + " · MicroPython " + version;
            IsConnected = true;
        }
        catch
        {
            await CloseCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    public Task<MicroPythonResult> ExecuteAsync(string code, CancellationToken token = default) =>
        InSessionAsync((raw, deadline) => raw.ExecuteAsync(code, deadline), TimeSpan.FromSeconds(30), token);

    /// <summary>执行板上已下载的脚本，持续交付原始输出；返回是否正常结束。回调在接收线程调用，不应阻塞。</summary>
    public Task<bool> RunScriptAsync(string relativePath, Action<string> output, CancellationToken token = default)
    {
        ValidateScriptPath(relativePath);
        ArgumentNullException.ThrowIfNull(output);
        return InSessionAsync(async (raw, cancellation) =>
        {
            var current = await ProjectService.ReadAsync(projectDirectory!, cancellation).ConfigureAwait(false);
            if (current.MicroPython != profile)
            {
                throw new StudioXException("MICROPYTHON_PROJECT", "连接后工程板型配置已改变，请重新连接。");
            }
            // 独立的主模块字典避免传输辅助变量污染用户程序，保留真实文件名以便定位异常。
            var code = "def _sx_run(path):\n with open(path) as script:\n  code = compile(script.read(), path, 'exec')\n exec(code, {'__name__': '__main__', '__file__': path})\n_sx_run(" + Quote(relativePath) + ")\ndel _sx_run";
            return await raw.RunAsync(code, output, cancellation).ConfigureAwait(false);
        }, Timeout.InfiniteTimeSpan, token);
    }

    public Task<MicroPythonUploadResult> UploadAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken token = default)
    {
        ValidateScriptPath(relativePath);
        if (content.Length > 256 * 1024)
        {
            throw new StudioXException("MICROPYTHON_FILE_SIZE", "单个脚本最多 256 KiB。");
        }
        // 调用者仍可能修改原缓冲区；整个传输只使用这一份不可变快照。
        var bytes = content.ToArray();
        return InSessionAsync(async (raw, deadline) =>
        {
            var root = projectDirectory!;
            var current = await ProjectService.ReadAsync(root, deadline).ConfigureAwait(false);
            if (current.MicroPython != profile)
            {
                throw new StudioXException("MICROPYTHON_PROJECT", "连接后工程板型配置已改变，请重新连接。");
            }
            async Task<string> Run(string code)
            {
                var result = await raw.ExecuteAsync(code, deadline).ConfigureAwait(false);
                RequireSuccess(result);
                return result.Output.Trim();
            }
            var path = Quote(relativePath);
            var sizeText = await Run($"import os, binascii, hashlib\ntry:\n _sx_size = os.stat({path})[6]\nexcept OSError as _sx_error:\n if _sx_error.args[0] != 2: raise\n _sx_size = -1\nprint(_sx_size)");
            if (!int.TryParse(sizeText, out var size) || size is < -1 or > 256 * 1024)
            {
                throw new StudioXException("MICROPYTHON_BACKUP", "原脚本大小无效或超过备份限制：" + sizeText);
            }
            string? backup = null;
            string? originalHash = null;
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            if (size >= 0)
            {
                using var original = new MemoryStream();
                for (var offset = 0; offset < size; offset += 512)
                {
                    var hex = await Run($"with open({path}, 'rb') as _sx_file:\n _sx_file.seek({offset})\n print(binascii.hexlify(_sx_file.read({Math.Min(512, size - offset)})).decode())");
                    var chunk = Convert.FromHexString(hex);
                    if (chunk.Length != Math.Min(512, size - offset))
                    {
                        throw new StudioXException("MICROPYTHON_BACKUP", "原脚本读取不完整，停止上传。");
                    }
                    original.Write(chunk);
                }
                var originalBytes = original.ToArray();
                originalHash = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
                if (await Run(HashScript(relativePath)) != originalHash)
                {
                    throw new StudioXException("MICROPYTHON_BACKUP", "原脚本在备份期间发生变化，停止上传。");
                }
                backup = PathBoundary.Resolve(root, $".studiox/micropython-backups/{stamp}/{relativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                await File.WriteAllBytesAsync(backup, originalBytes, deadline).ConfigureAwait(false);
                await JsonStore.WriteAsync(backup + ".json", new
                {
                    remotePath = relativePath,
                    originalSha256 = originalHash,
                    originalBytes = size,
                    board = profile!.Board,
                    interpreterVersion = profile.Version,
                    backedUpAtHostUtc = DateTimeOffset.UtcNow
                }, deadline).ConfigureAwait(false);
            }
            var temporary = relativePath + ".studiox-" + stamp + ".tmp";
            Diagnostic?.Invoke("上传临时文件：" + temporary + "；本地备份：" + (backup ?? "板上目标原先不存在"));
            var parent = relativePath.Split('/').SkipLast(1).ToArray();
            for (var i = 1; i <= parent.Length; i++)
            {
                var folder = Quote(string.Join('/', parent.Take(i)));
                await Run($"try:\n os.mkdir({folder})\nexcept OSError:\n if not os.stat({folder})[0] & 0x4000: raise");
            }
            await Run($"with open({Quote(temporary)}, 'wb') as _sx_file:\n pass");
            for (var offset = 0; offset < bytes.Length; offset += 512)
            {
                var hex = Convert.ToHexString(bytes.AsSpan(offset, Math.Min(512, bytes.Length - offset)));
                await Run($"with open({Quote(temporary)}, 'ab') as _sx_file:\n _sx_chunk = binascii.unhexlify('{hex}')\n if _sx_file.write(_sx_chunk) != len(_sx_chunk): raise OSError('short write')");
            }
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (await Run(HashScript(temporary)) != hash)
            {
                throw new StudioXException("MICROPYTHON_VERIFY", "临时脚本 SHA-256 不匹配；原脚本保持不变，临时文件：" + temporary);
            }
            if (originalHash is not null && await Run(HashScript(relativePath)) != originalHash)
            {
                throw new StudioXException("MICROPYTHON_CONFLICT", "板上脚本在上传期间改变，停止替换；临时文件：" + temporary);
            }
            if (originalHash is null)
            {
                await Run($"try:\n os.stat({path})\nexcept OSError as _sx_error:\n if _sx_error.args[0] != 2: raise\nelse:\n raise OSError('destination appeared during upload')");
            }
            await Run($"os.rename({Quote(temporary)}, {path})\nos.sync()");
            if (await Run(HashScript(relativePath)) != hash)
            {
                throw new StudioXException("MICROPYTHON_VERIFY", "替换后脚本校验失败。备份：" + (backup ?? "目标原先不存在"));
            }
            return new MicroPythonUploadResult(relativePath, bytes.Length, hash, backup);
        }, TimeSpan.FromMinutes(3), token);
    }

    public static void ValidateScriptPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 160 || !relativePath.EndsWith(".py", StringComparison.Ordinal) ||
            relativePath.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.StartsWith('.') ||
                part.Any(value => !char.IsLetterOrDigit(value) && value is not ('_' or '-' or '.'))))
        {
            throw new StudioXException("MICROPYTHON_PATH", "脚本必须使用工程内相对 .py 路径，不能包含隐藏目录、反斜杠或上级目录。");
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string HashScript(string path) => $"_sx_hash = hashlib.sha256()\nwith open({Quote(path)}, 'rb') as _sx_file:\n while True:\n  _sx_chunk = _sx_file.read(512)\n  if not _sx_chunk: break\n  _sx_hash.update(_sx_chunk)\nprint(binascii.hexlify(_sx_hash.digest()).decode())";
    private static void RequireSuccess(MicroPythonResult result)
    {
        if (result.Error.Length != 0)
        {
            throw new StudioXException("MICROPYTHON_REMOTE", result.Output + result.Error);
        }
    }

    private async Task<T> InSessionAsync<T>(Func<RawReplClient, CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsConnected || client is null || lifetime is null)
            {
                throw new StudioXException("MICROPYTHON_DISCONNECTED", "请先连接匹配当前工程的 MicroPython 设备。");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(timeout);
            return await action(client, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            // 超时、取消或协议错位后不复用连接，避免把迟到输出误认成下一条命令。
            await CloseCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        lock (sync)
        {
            lifetime?.Cancel();
        }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task CloseCoreAsync()
    {
        IsConnected = false;
        Identity = "未连接";
        if (session is { } closingSession)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await closingSession.SendAsync(new byte[] { 3, 3, 2 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Diagnostic?.Invoke(ex.ToString()); }
            await closingSession.DisposeAsync().ConfigureAwait(false);
        }
        subscription?.Dispose();
        subscription = null;
        session = null;
        client = null;
        profile = null;
        projectDirectory = null;
        lock (sync)
        {
            lifetime?.Dispose();
            lifetime = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }
}

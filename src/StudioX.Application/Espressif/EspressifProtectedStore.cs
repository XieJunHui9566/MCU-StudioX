namespace StudioX.Application.Espressif;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

/// <summary>使用当前 Windows 用户的 DPAPI 保护服务专属缓存，并串行化不同宿主的文件事务。</summary>
internal sealed class EspressifProtectedStore : ITokenCache
{
    private readonly string directory;
    private readonly string mutexName;
    private readonly byte[] entropy = Encoding.UTF8.GetBytes("MCUStudioX:EspressifDocs:v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string? expectedGeneration;

    public EspressifProtectedStore(string dataDirectory)
    {
        directory = Path.Combine(Path.GetFullPath(dataDirectory), "secure", "espressif-docs");
        mutexName = "Local\\MCUStudioX.EspressifDocs." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ReadForOperation<TokenContainer>("oauth.dat", cancellationToken));

    public string BeginOperation(CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        var generation = ReadCore<string>("generation.dat");
        if (generation is null)
        {
            generation = Guid.NewGuid().ToString("N");
            WriteCore("generation.dat", generation);
        }
        expectedGeneration = generation;
        return generation;
    }

    public string OperationGeneration => expectedGeneration ?? throw new EspressifAuthorizationRevokedException();

    public void StoreNewAuthorization(TokenContainer tokens, CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        CheckGeneration();
        // 新授权的首次令牌与新代次在同一互斥事务提交，旧宿主刷新不能覆盖新的 DCR 身份。
        var generation = Guid.NewGuid().ToString("N");
        WriteCore("generation.dat", generation);
        WriteCore("oauth.dat", tokens);
        expectedGeneration = generation;
    }

    public T? ReadForOperation<T>(string name, CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        CheckGeneration();
        return ReadCore<T>(name);
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
    {
        Write("oauth.dat", tokens, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public T? Read<T>(string name, CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        return ReadCore<T>(name);
    }

    private T? ReadCore<T>(string name)
    {
        var path = FilePath(name);
        if (!File.Exists(path))
        {
            return default;
        }
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            if (name == "results.dat")
            {
                // 旧缓存可能采用条目数限制而超过字节预算；删除可重建资料，不删除授权。
                WriteCore("diagnostic.dat", new { observedAtUtc = DateTimeOffset.UtcNow, code = "ESPRESSIF_CACHE_OVERSIZE" });
                File.Delete(path);
                return default;
            }
            throw new InvalidDataException("乐鑫安全缓存超过允许大小。");
        }
        var encrypted = File.ReadAllBytes(path);
        var plain = Transform(encrypted, protect: false);
        try
        {
            return JsonSerializer.Deserialize<T>(plain, Json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Write<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        if (name is "oauth.dat" or "results.dat")
        {
            CheckGeneration();
        }
        WriteCore(name, value);
    }

    private void WriteCore<T>(string name, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (plain.Length > 3 * 1024 * 1024)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidDataException("乐鑫安全缓存超过写入预算。");
        }
        byte[] encrypted;
        try
        {
            encrypted = Transform(plain, protect: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        Directory.CreateDirectory(directory);
        var destination = FilePath(name);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void WriteResults<T>(List<T> results, CancellationToken cancellationToken = default)
    {
        // 按真实序列化字节淘汰，中文的 JSON 转义和字符串嵌套也纳入预算。
        while (results.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(results, Json).Length > 3 * 1024 * 1024)
        {
            results.RemoveAt(0);
        }
        Write("results.dat", results, cancellationToken);
    }

    public void Clear(CancellationToken cancellationToken = default)
    {
        using var mutex = Acquire(cancellationToken);
        // 撤销先持久化新代次；旧宿主在途刷新和查询结果不能在删除后复活授权。
        WriteCore("generation.dat", Guid.NewGuid().ToString("N"));
        // 只删除本服务确定的缓存名称，不递归清理用户数据目录。
        foreach (var name in new[] { "oauth.dat", "results.dat", "diagnostic.dat" })
        {
            File.Delete(FilePath(name));
        }
    }

    private string FilePath(string name)
    {
        if (name is not ("oauth.dat" or "results.dat" or "diagnostic.dat" or "generation.dat"))
        {
            throw new ArgumentException("未知安全缓存名称。", nameof(name));
        }
        return Path.Combine(directory, name);
    }

    private void CheckGeneration()
    {
        if (expectedGeneration is null || ReadCore<string>("generation.dat") != expectedGeneration)
        {
            throw new EspressifAuthorizationRevokedException();
        }
    }

    private MutexLease Acquire(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("乐鑫授权安全存储需要 Windows。");
        }
        var mutex = new Mutex(false, mutexName);
        try
        {
            try
            {
                if (WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(10)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("乐鑫安全缓存正被其他宿主使用。");
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一宿主异常退出；写入通过原子替换完成，继续读取完整文件。
            }
            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = Allocate(input);
        var entropyBlob = Allocate(entropy);
        DataBlob output = default;
        try
        {
            // 未设置 LOCAL_MACHINE，令牌和注册凭据仅能由当前 Windows 用户解密。
            var success = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success)
            {
                throw new CryptographicException("Windows 无法处理乐鑫安全缓存。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            var result = new byte[output.Length];
            Marshal.Copy(output.Pointer, result, 0, output.Length);
            return result;
        }
        finally
        {
            ClearBlob(inputBlob, local: false);
            ClearBlob(entropyBlob, local: false);
            ClearBlob(output, local: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Length = bytes.Length, Pointer = pointer };
    }

    private static void ClearBlob(DataBlob blob, bool local)
    {
        if (blob.Pointer == IntPtr.Zero)
        {
            return;
        }
        Marshal.Copy(new byte[blob.Length], 0, blob.Pointer, blob.Length);
        if (local)
        {
            LocalFree(blob.Pointer);
        }
        else
        {
            Marshal.FreeHGlobal(blob.Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Pointer;
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class EspressifAuthorizationRevokedException : Exception
{
    public EspressifAuthorizationRevokedException() : base("乐鑫文档授权已由另一宿主撤销，请显式重新连接。")
    {
    }
}

namespace StudioX.Application;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>API Key 只保存于当前 Windows 用户的凭据管理器，不写入工程、JSON 或日志。</summary>
public sealed class AiCredentialStore
{
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public void SetApiKey(string apiKey) => SetApiKey(apiKey, new AiSettings().BaseUrl);

    public void SetApiKey(string apiKey, string baseUrl)
    {
        EnsureWindows();
        var target = TargetFor(baseUrl);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(c => c is < '!' or > '~'))
            throw new StudioXException("AI_API_KEY", "API Key 无效。");
        var bytes = StrictUtf8.GetBytes(apiKey);
        IntPtr blob = IntPtr.Zero;
        try
        {
            if (bytes.Length > 2560) throw new StudioXException("AI_API_KEY", "API Key 过长。");
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = "MCU StudioX"
            };
            if (!CredWrite(ref credential, 0)) throw NativeError("AI_CREDENTIAL_SAVE", "无法保存 AI API Key。");
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
            Array.Clear(bytes);
        }
    }

    public bool HasApiKey() => HasApiKey(new AiSettings().BaseUrl);

    public bool HasApiKey(string baseUrl)
    {
        EnsureWindows();
        if (!TryRead(TargetFor(baseUrl), out var pointer)) return false;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlobSize is > 0 and <= 2560 && credential.CredentialBlob != IntPtr.Zero;
        }
        finally { CredFree(pointer); }
    }

    public void DeleteApiKey() => DeleteApiKey(new AiSettings().BaseUrl);

    public void DeleteApiKey(string baseUrl)
    {
        EnsureWindows();
        if (!CredDelete(TargetFor(baseUrl), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw NativeError("AI_CREDENTIAL_DELETE", "无法删除 AI API Key。");
    }

    internal string? GetApiKey(string baseUrl)
    {
        EnsureWindows();
        if (!TryRead(TargetFor(baseUrl), out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlob == IntPtr.Zero)
                throw new StudioXException("AI_CREDENTIAL", "保存的 AI API Key 无效，请重新设置。");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new StudioXException("AI_CREDENTIAL", "保存的 AI API Key 无效，请重新设置。");
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static bool TryRead(string target, out IntPtr pointer)
    {
        if (CredRead(target, Generic, 0, out pointer)) return true;
        if (Marshal.GetLastWin32Error() == NotFound) return false;
        throw NativeError("AI_CREDENTIAL_READ", "无法读取 AI API Key。");
    }

    private static string TargetFor(string baseUrl)
    {
        var endpoint = AiSettingsService.CompletionUri(new AiSettings(BaseUrl: baseUrl));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri));
        return "MCUStudioX:AI:" + Convert.ToHexString(hash);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new StudioXException("AI_CREDENTIAL_PLATFORM", "AI API Key 安全存储需要 Windows。");
    }

    private static StudioXException NativeError(string code, string message) =>
        new(code, message + "（Windows 错误 " + Marshal.GetLastWin32Error() + "）。");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr credential);
}

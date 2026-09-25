namespace StudioX.Application;

using System.Runtime.InteropServices;
using System.Text;
using StudioX.Foundation;

/// <summary>Tavily 密钥只保存在当前 Windows 用户凭据中，与模型密钥隔离。</summary>
public sealed class WebCredentialStore
{
    private const string Target = "MCUStudioX:WEB:TAVILY";
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public void SetApiKey(string apiKey)
    {
        EnsureWindows();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 2560 || apiKey.Any(c => c is < '!' or > '~'))
            throw new StudioXException("WEB_API_KEY", "Tavily API Key 格式无效。");
        var bytes = StrictUtf8.GetBytes(apiKey);
        IntPtr blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = Target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = "MCU StudioX"
            };
            if (!CredWrite(ref credential, 0)) throw NativeError("WEB_CREDENTIAL_SAVE", "无法保存 Tavily API Key。");
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

    public bool HasApiKey()
    {
        EnsureWindows();
        if (!TryRead(out var pointer)) return false;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlobSize is > 0 and <= 2560 && credential.CredentialBlob != IntPtr.Zero;
        }
        finally { CredFree(pointer); }
    }

    public void DeleteApiKey()
    {
        EnsureWindows();
        if (!CredDelete(Target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw NativeError("WEB_CREDENTIAL_DELETE", "无法删除 Tavily API Key。");
    }

    internal string? GetApiKey()
    {
        EnsureWindows();
        if (!TryRead(out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlob == IntPtr.Zero)
                throw new StudioXException("WEB_CREDENTIAL", "保存的 Tavily API Key 无效，请重新设置。");
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new StudioXException("WEB_CREDENTIAL", "保存的 Tavily API Key 无效，请重新设置。");
            }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static bool TryRead(out IntPtr pointer)
    {
        if (CredRead(Target, Generic, 0, out pointer)) return true;
        if (Marshal.GetLastWin32Error() == NotFound) return false;
        throw NativeError("WEB_CREDENTIAL_READ", "无法读取 Tavily API Key。");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new StudioXException("WEB_CREDENTIAL_PLATFORM", "Tavily API Key 安全存储需要 Windows。");
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

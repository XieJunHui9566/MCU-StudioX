namespace StudioX.Foundation;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>跨进程工具目录租约；共享使用与独占维护互斥，进程退出时由系统释放。</summary>
public sealed class ToolUsageLease : IDisposable
{
    private readonly FileStream stream;
    private ToolUsageLease(FileStream stream) => this.stream = stream;
    public static ToolUsageLease Acquire(string toolRoot, bool maintenance = false)
    {
        var path = LeasePath(toolRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, maintenance ? FileShare.None : FileShare.ReadWrite)); }
        catch (IOException error) { throw new StudioXException("TOOLS_BUSY", "工具正在使用或维护，请等待操作结束后重试。", error); }
    }
    public static bool IsBusy(string toolRoot)
    {
        var path = LeasePath(toolRoot);
        if (!File.Exists(path)) return false;
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    public static ToolUsageLease? ForExecutable(string executable)
    {
        for (var directory = Directory.GetParent(Normalize(executable)); directory?.Parent?.Parent is not null; directory = directory.Parent)
            if (directory.Parent.Parent.Name.Equals("toolsets", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory.FullName, "toolset.json")))
                return Acquire(directory.FullName);
        return null;
    }
    private static string LeasePath(string toolRoot)
    {
        var identity = Normalize(toolRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        // 租约不写入受哈希保护的工具目录；临时目录按当前用户隔离。
        return Path.Combine(Path.GetTempPath(), "StudioX-tool-leases", key + ".lock");
    }
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        var existing = full;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing) return full;
            existing = parent;
        }
        using var handle = CreateFile(existing, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return full;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return full;
        var resolved = buffer.ToString();
        resolved = resolved.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal) ? "\\\\" + resolved[8..]
            : resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ? resolved[4..] : resolved;
        return existing == full ? resolved : Path.Combine(resolved, Path.GetRelativePath(existing, full));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder buffer, uint capacity, uint flags);
    public void Dispose() => stream.Dispose();
}

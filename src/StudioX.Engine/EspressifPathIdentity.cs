namespace StudioX.Engine;

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>只用于校验原生工具回报的路径；Windows 短路径或目录别名必须指向同一实际文件。</summary>
public static class EspressifPathIdentity
{
    internal static bool AreEqual(string first, string second) => NormalizePath(first).Equals(NormalizePath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!OperatingSystem.IsWindows() || !File.Exists(full) && !Directory.Exists(full))
        {
            return full;
        }
        using var handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return full;
        }
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity)
        {
            return full;
        }
        var resolved = buffer.ToString();
        return resolved.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal) ? "\\\\" + resolved[8..]
            : resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint attributes, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

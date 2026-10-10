namespace StudioX.Foundation;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>短暂只读获取文件身份与创建时间；不持有会阻止 Windows 原子替换或目录移动的句柄。</summary>
public sealed record WindowsFileIdentity(string Value)
{
    public static WindowsFileIdentity? Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        // FILE_READ_ATTRIBUTES、共享读写删除、OPEN_EXISTING、BACKUP_SEMANTICS + OPEN_REPARSE_POINT。
        using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3)
            {
                return null;
            }
            throw new Win32Exception(error, "读取文件身份失败：" + path);
        }
        if (!GetFileInformationByHandleEx(handle, 18, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, "读取 FILE_ID_INFO 失败：" + path);
        }
        if (!GetBasicInformation(handle, 0, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "读取 FILE_BASIC_INFO 失败：" + path);
        }
        return new($"{info.Volume:x16}:{info.Low:x16}{info.High:x16}:{basic.Creation:x16}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong Volume, Low, High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long Creation, Access, Write, Change;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInformation(SafeFileHandle handle, int informationClass, out FileBasicInfo info, uint size);
}

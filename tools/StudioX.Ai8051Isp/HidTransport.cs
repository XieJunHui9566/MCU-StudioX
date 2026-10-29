using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StudioX.Ai8051Isp;

// Windows 为此 consumer HID 保留共享句柄；用进程间文件租约串行化本工具的会话。
// 其他不遵守租约的 HID 软件仍须关闭，不能把共享打开宣称为系统级独占。
public sealed class HidTransport : IHidReportTransport, IDisposable
{
    private readonly FileStream stream;
    private readonly FileStream lease;
    private readonly Action<string, byte[]> trace;
    public HidTransport(HidDevice device, Action<string, byte[]> trace)
    {
        if (device.InputBytes != 65 || device.OutputBytes != 65)
        {
            throw new InvalidDataException("Expected 64-byte HID reports plus report ID.");
        }
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (process.ProcessName.StartsWith("AiCube-ISP", StringComparison.OrdinalIgnoreCase) || process.ProcessName.StartsWith("AIapp-ISP", StringComparison.OrdinalIgnoreCase) || process.ProcessName.StartsWith("STC-ISP", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Close the official ISP before opening a script session.");
                }
            }
        }
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "DeviceLocks");
        Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(device.Path.ToUpperInvariant())));
        lease = new FileStream(System.IO.Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var handle = Native.CreateFileW(device.Path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            lease.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open STC HID.");
        }
        try
        {
            stream = new FileStream(handle, FileAccess.ReadWrite, 65, true);
        }
        catch { handle.Dispose(); lease.Dispose(); throw; }
        this.trace = trace;
    }

    public async Task Write(byte[] report, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(3000);
        try
        {
            await stream.WriteAsync(report, timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("HID write did not complete within 3000 ms.", error);
        }
        trace("tx", report);
    }

    public async Task<byte[]> Read(int timeoutMs, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        var report = new byte[65];
        int count;
        try
        {
            count = await stream.ReadAsync(report, timeout.Token);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"No HID response within {timeoutMs} ms.", error);
        }
        trace("rx", report[..count]);
        if (count != 65)
        {
            throw new InvalidDataException($"Short HID report: {count}/65.");
        }
        return report;
    }

    public void Dispose()
    {
        stream.Dispose();
        lease.Dispose();
    }

    public static IReadOnlyList<HidDevice> Enumerate()
    {
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var devices = new List<HidDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var entry = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref entry))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259)
                    {
                        break;
                    }
                    throw new Win32Exception(error);
                }
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref entry, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref entry, detail, size, out _, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    if (!path.Contains("vid_34bf&pid_ff0f", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    using var handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var attributes = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attributes))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    if (!Native.HidD_GetPreparsedData(handle, out var preparsed))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var caps = new byte[64];
                    try
                    {
                        if (Native.HidP_GetCaps(preparsed, caps) != 0x110000)
                        {
                            throw new InvalidDataException("HID caps failed.");
                        }
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                    var text = new byte[256];
                    Native.HidD_GetProductString(handle, text, text.Length);
                    devices.Add(new(path, attributes.VendorId, attributes.ProductId,
                        System.Text.Encoding.Unicode.GetString(text).TrimEnd('\0'), BitConverter.ToUInt16(caps, 4), BitConverter.ToUInt16(caps, 6)));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct InterfaceData
        {
            public uint Size; public Guid ClassGuid; public uint Flags; public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Attributes
        {
            public int Size; public ushort VendorId, ProductId, Version;
        }
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, [Out] byte[] caps);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] byte[] data, int length);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}

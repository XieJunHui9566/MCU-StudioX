namespace StudioX.Engine.Pic;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

/// <summary>从 USB 描述符恢复 IPECMD 列表隐藏的序列号尾部空格，不发送 HID 报告或连接目标。</summary>
public static class PicProgrammerIdentity
{
    public static string Resolve(string selectedSerial)
    {
        var serials = new List<string>();
        // 复用现有按 VID/PID 枚举 HID 的只读入口；不创建 ISP 传输会话。
        foreach (var device in HidTransport.Enumerate(0x04d8, 0x900a))
        {
            using var handle = Native.CreateFileW(device.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            var buffer = new byte[256];
            if (handle.IsInvalid || !Native.HidD_GetSerialNumberString(handle, buffer, buffer.Length))
            {
                throw new StudioXException("PIC_PROGRAMMER_ID", "无法读取 PICkit3 的 USB 序列号，未启动下载工具。",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            serials.Add(Encoding.Unicode.GetString(buffer).TrimEnd('\0'));
        }
        return Resolve(selectedSerial, serials);
    }

    public static string Resolve(string selectedSerial, IReadOnlyList<string> usbSerials)
    {
        // IPECMD 6.20 打印列表时 trim()，但 -TS 查找时不 trim()；必须使用描述符原值，不能猜测填充。
        var matches = usbSerials.Where(serial => serial.TrimEnd(' ').Equals(selectedSerial, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new StudioXException("PIC_PROGRAMMER_ID", matches.Length == 0
                ? "选定的 PICkit3 已断开或 USB 序列号不匹配，请重新选择烧录器。"
                : "多个 PICkit3 的显示序列号相同，无法唯一选择；请只连接本次使用的烧录器。");
        }
        if (matches[0].Length > 64 || matches[0].Any(character => character != ' ' &&
                !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
        {
            throw new StudioXException("PIC_PROGRAMMER_ID", "PICkit3 的 USB 序列号包含不支持的字符，未启动下载工具。");
        }
        return matches[0];
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, int length);
    }
}

namespace StudioX.Engine.Hdl;

using System.Runtime.InteropServices;
using System.Text;
using StudioX.Foundation;

/// <summary>Icarus Windows 驱动调用子工具时要求库目录不含空格。</summary>
internal static class IcarusToolPath
{
    internal static string ForLibraryDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || !full.Any(char.IsWhiteSpace)) {return full;}
        // 只使用已核实目录的 Windows 别名，不复制或修改已锁定的工具内容。
        var buffer = new StringBuilder(32768);
        var count = GetShortPathName(full, buffer, (uint)buffer.Capacity);
        var native = count > 0 && count < buffer.Capacity ? buffer.ToString() : full;
        if (native.Any(char.IsWhiteSpace))
        {
            throw new StudioXException("HDL_TOOL_PATH", "内置 Icarus 的子工具目录含空格，且当前磁盘没有可用的 Windows 短路径：" + full);
        }
        return native;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, uint length);
}

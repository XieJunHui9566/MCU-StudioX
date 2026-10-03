namespace StudioX.Engine;

using System.Runtime.InteropServices;
using System.Text;
using StudioX.Foundation;

/// <summary>旧版 Xtensa 工具仍有路径约束；优先使用同一目录的 Windows 短名称，不搬移用户工程。</summary>
internal static class EspressifNativePath
{
    internal static string ForExecutable(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows()) return full;
        // Xtensa 启动器根据原始文件名选择配置；文件名变为 8.3 别名会使子编译器配置冲突。
        var native = Path.Combine(For(Path.GetDirectoryName(full)!), Path.GetFileName(full));
        Validate(native, full);
        return native;
    }

    internal static string For(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }
        var buffer = new StringBuilder(32768);
        var count = GetShortPathName(full, buffer, (uint)buffer.Capacity);
        var native = count > 0 && count < buffer.Capacity ? buffer.ToString() : full;
        Validate(native, full);
        return native;
    }

    private static void Validate(string native, string full)
    {
        if (native.Any(character => character > 127 || char.IsWhiteSpace(character)) || native.Contains(';'))
        {
            throw new StudioXException("ESPRESSIF_PATH", "此 IDF 开发环境组件需要不含空格、分号或非 ASCII 字符的路径，" +
                "当前磁盘没有可用的 Windows 短路径：" + full + "。工程内容与 SDK 配置未改动。");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, uint length);
}

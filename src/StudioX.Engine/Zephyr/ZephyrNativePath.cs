namespace StudioX.Engine.Zephyr;

using System.Runtime.InteropServices;
using System.Text;
using StudioX.Foundation;

/// <summary>Zephyr 的 Kconfig 脚本要求无空格 ASCII 路径；不改变用户目录名和系统设置。</summary>
internal static class ZephyrNativePath
{
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            var buffer = new StringBuilder(32768);
            if (GetShortPathName(full, buffer, buffer.Capacity) > 0 &&
                buffer.ToString().All(c => c is > ' ' and < '\x7f' && c != ';'))
            {
                return buffer.ToString().Replace('\\', '/');
            }
        }
        if (full.All(c => c is > ' ' and < '\x7f' && c != ';'))
        {
            return full.Replace('\\', '/');
        }
        throw new StudioXException("ZEPHYR_PATH", "Zephyr 需要组件和用户缓存目录具有无空格的 ASCII 路径或 Windows 短路径。请在开发环境组件管理中迁移组件位置，不会更改系统配置：" + full);
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);
}

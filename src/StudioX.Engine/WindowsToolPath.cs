namespace StudioX.Engine;

using System.Runtime.InteropServices;
using System.Text;
using StudioX.Foundation;

/// <summary>旧式工具使用同一目录的 Windows 短名，不修改系统配置或移动用户工程。</summary>
internal static class WindowsToolPath
{
    public static string Compatible(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || full.All(character => character is > ' ' and < '\x7f' && character is not ';' and not '\''))
        {
            return full;
        }
        var buffer = new StringBuilder(32768);
        var count = GetShortPathName(full, buffer, (uint)buffer.Capacity);
        var result = buffer.ToString();
        if (count == 0 || count >= buffer.Capacity || result.Any(character => character <= ' ' || character >= '\x7f' || character is ';' or '\''))
        {
            throw new StudioXException("TOOL_PATH", "工具需要兼容的私有执行路径，当前目录没有可用的 Windows 短名：" + full);
        }
        return result;
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder shortPath, uint length);
}

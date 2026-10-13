namespace StudioX.Foundation;

using System.Runtime.InteropServices;

/// <summary>宿主身份与文件名规则；组件元数据仍显式锁定平台，不按本机工具猜测需求。</summary>
public static class HostPlatform
{
    public static string Current => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unsupported")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static bool IsSupported(string host) => host is "win-x64" or "linux-x64";

    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ExecutableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
}

namespace StudioX.Engine;

using System.Reflection;
using StudioX.Foundation;

/// <summary>基础映射的固定身份、保护和布局检查属于下载实现，不能由工程文本替换。</summary>
public static class Ag32PinMappingTargetScript
{
    private static readonly Lazy<string> Canonical = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StudioX.Engine.Ag32PinMapping.Target.cfg")
            ?? throw new StudioXException("AG32_MAPPING_CONFIG", "缺少内置 AG32 下载检查脚本。");
        using var reader = new StreamReader(stream);
        return Normalize(reader.ReadToEnd());
    });

    // 新系列包按完整料号命名；仅兼容同一已验证目标的两个路径，仍核对全部脚本内容。
    public static bool IsSupportedPath(string path) => path is "debug/ag32vf303.cfg" or "debug/ag32vf303cct6.cfg";

    public static string RequireCompatible(string projectDirectory, string targetScript = "debug/ag32vf303.cfg")
    {
        if (!IsSupportedPath(targetScript))
        {
            throw new StudioXException("AG32_MAPPING_CONFIG", "AG32 下载检查脚本路径不属于已验证的 AG32VF303CCT6 器件包。");
        }
        var path = PathBoundary.Resolve(projectDirectory, "device/" + targetScript);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 ||
            !string.Equals(Normalize(File.ReadAllText(path)), Canonical.Value, StringComparison.Ordinal))
        {
            throw new StudioXException("AG32_MAPPING_CONFIG", "AG32 下载检查脚本已变化或版本不匹配；请恢复匹配器件包的配置后下载映射。");
        }
        // 实际进程执行内置内容；检查后外部改写工程文件也不能删除保护或加入选项字节操作。
        return Canonical.Value;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
}

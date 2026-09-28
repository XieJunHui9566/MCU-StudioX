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

    public static string RequireCompatible(string projectDirectory)
    {
        var path = PathBoundary.Resolve(projectDirectory, "device/debug/ag32vf303.cfg");
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

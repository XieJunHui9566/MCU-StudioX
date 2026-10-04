namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>预装与导入组件使用相同身份；组件包版本与内部 GCC/SDK 版本分别记录。</summary>
public sealed record DevelopmentComponentIdentity(string Id, string Version, string Host, string CompilerId)
{
    public string Key => $"{Id}/{Version}/{Host}";
    public string RelativeDirectory => $"{Id}/{Version}";

    public static DevelopmentComponentIdentity FromManifest(ToolsetManifest manifest)
    {
        PackValidator.Token(manifest.Id);
        PackValidator.Version(manifest.Version);
        if (manifest.FormatVersion != 1 || manifest.Host != "win-x64" || string.IsNullOrWhiteSpace(manifest.CompilerId))
        {
            throw new StudioXException("TOOLS_IDENTITY", "开发环境组件格式或宿主不支持，或编译器身份缺失。");
        }
        return new(manifest.Id, manifest.Version, manifest.Host, manifest.CompilerId);
    }
}

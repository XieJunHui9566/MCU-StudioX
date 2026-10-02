namespace StudioX.Application.Tools;

public sealed record ToolArchivePreview(string Archive, string ArchiveSha256, string Id, string Version, string CompilerId,
    string Name, long Bytes, int Files, string Components, IReadOnlyList<string> InstalledVersions, string Fingerprint)
{
    public string ToText() => $"{Name}\n{Id} / {Version}\n编译器：{CompilerId}\n归档展开：{Bytes / 1073741824d:F2} GiB · {Files:N0} 个文件\n组件：{Components}\n"
        + "已有版本：" + (InstalledVersions.Count == 0 ? "无" : string.Join("、", InstalledVersions))
        + "\n\n新版本并存安装，保留所有已有版本。现有工程、器件包及内容锁不会自动改用新版本。\n安装时校验全部索引文件，不执行归档中的程序。";
}

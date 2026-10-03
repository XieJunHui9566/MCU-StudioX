namespace StudioX.Application.Tools;

using StudioX.Engine;

public sealed record ToolArchivePreview(string Archive, string ArchiveSha256, string Id, string Version, string CompilerId,
    string Name, long Bytes, int Files, string Components, IReadOnlyList<string> InstalledVersions, string Fingerprint,
    bool AlreadyInstalled = false, string Host = "win-x64")
{
    public DevelopmentComponentIdentity Identity => new(Id, Version, Host, CompilerId);
    public string ToText() => $"{Name}\n开发环境组件：{Identity.Key}\n编译器：{CompilerId}\n归档展开：{Bytes / 1073741824d:F2} GiB · {Files:N0} 个文件\n内部开发环境组件版本：{Components}\n"
        + "已有版本：" + (InstalledVersions.Count == 0 ? "无" : string.Join("、", InstalledVersions))
        + (AlreadyInstalled ? "\n\n相同组件已安装。确认后完整校验归档与现有组件，校验通过后跳过安装。"
            : "\n\n新版本并存安装，保留所有已有版本。")
        + "现有工程、器件包及内容锁不会自动改用新版本。\n校验全部索引文件，不执行归档中的程序。";
}

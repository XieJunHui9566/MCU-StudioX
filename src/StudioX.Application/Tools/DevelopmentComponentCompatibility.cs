namespace StudioX.Application.Tools;

public enum ComponentProjectCompatibility { NoProject, Unrelated, ExactRequirement, MigrationRequired, Incompatible }

/// <summary>只读升级预览。组件并存安装与工程迁移是两个独立操作。</summary>
public sealed record DevelopmentComponentCompatibility(string ArchiveSha256, string? ProjectDirectory, string? ProjectFingerprint,
    ComponentProjectCompatibility State, string Summary, IReadOnlyList<string> Differences, IReadOnlyList<string> Blockers,
    int RemovedFiles, IReadOnlyList<string> RemovedFileExamples)
{
    public bool CanInstall => State != ComponentProjectCompatibility.Incompatible;
    public string ToText() => Summary + "\n\n" + string.Join('\n', Differences)
        + (Blockers.Count == 0 ? "" : "\n\n工程使用前需要处理：\n" + string.Join('\n', Blockers))
        + (RemovedFiles == 0 ? "" : $"\n\n相对原组件移除 {RemovedFiles:N0} 个索引文件（路径样例）：\n" + string.Join('\n', RemovedFileExamples))
        + "\n\n安装仅新增或校验组件，保留工程源码、SDK 配置、器件声明、原组件和内容锁。预览只检查元数据；实际兼容性需要工程编译验证。";
}

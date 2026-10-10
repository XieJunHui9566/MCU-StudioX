namespace StudioX.Application.Editing;

/// <summary>一次合并后的磁盘变化；溢出或手动刷新要求重新核对完整工作区。</summary>
public sealed record ProjectChangeBatch(string Directory, long Revision, IReadOnlyList<ProjectFileChange> Changes,
    bool RequiresRescan = false, string? Diagnostic = null)
{
    public bool NamesChanged => RequiresRescan || Changes.Any(change => change.Kind != ProjectFileChangeKind.Changed);
    public bool AnalysisChanged => NamesChanged || Changes.Any(change => ProjectChangePolicy.IsAnalysisInput(change.Path) ||
        change.PreviousPath is { } previous && ProjectChangePolicy.IsAnalysisInput(previous));
}

namespace StudioX.Engine;

/// <summary>图形视图的引用类型；远端分支只是本地跟踪引用，不表示远端实时状态。</summary>
public enum GitRefKind
{
    LocalBranch, RemoteBranch, Tag
}

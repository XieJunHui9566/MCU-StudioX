namespace StudioX.KeilImporter;

/// <summary>编译取消时保留已经发布的工程身份，让面板仍能打开副本继续开发。</summary>
public sealed class ProjectCreationCanceledException(CreationResult result, OperationCanceledException inner)
    : OperationCanceledException(result.BuildError, inner, inner.CancellationToken)
{
    public CreationResult Result { get; } = result;
}

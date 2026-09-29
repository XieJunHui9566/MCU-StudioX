namespace StudioX.Application.CodeIntelligence;

using StudioX.Application.Editing;

/// <summary>语言服务建议的可预览修改，不执行建议携带的外部命令。</summary>
public sealed record CodeActionPlan(string Title, IReadOnlyList<WorkspaceFileChange> Changes);

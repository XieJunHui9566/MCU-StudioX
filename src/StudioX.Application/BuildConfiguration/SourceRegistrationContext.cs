namespace StudioX.Application.BuildConfiguration;

using StudioX.Application.Editing;

/// <summary>预览绑定工程身份、磁盘保存基线及当前 CMake 缓冲区。</summary>
public sealed record SourceRegistrationContext(string ProjectDirectory, string ProjectHash, WorkspaceBufferSnapshot Configuration,
    bool WasOpen, bool EspIdf, IReadOnlyList<SourceRegistrationTarget> Targets);

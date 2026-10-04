namespace StudioX.Application.PeripheralDevelopment;

using StudioX.Application.Editing;

/// <summary>保留磁盘基线及未保存文本，预览不覆盖用户正在编辑的组件配置。</summary>
public sealed record PeripheralComponentContext(WorkspaceBufferSnapshot Source, WorkspaceBufferSnapshot CMake,
    bool SourceWasOpen, bool CMakeWasOpen);

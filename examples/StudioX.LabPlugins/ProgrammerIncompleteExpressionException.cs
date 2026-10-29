namespace StudioX.LabPlugins;

/// <summary>编辑中的合法前缀不作为实时计算错误；显式求值仍按输入错误处理。</summary>
internal sealed class ProgrammerIncompleteExpressionException(string message) : ArgumentException(message);

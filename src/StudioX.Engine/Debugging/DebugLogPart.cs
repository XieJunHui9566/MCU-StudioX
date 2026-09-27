namespace StudioX.Engine.Debugging;

/// <summary>文本中的 {只读表达式} 由调试器求值；双花括号表示字面花括号。</summary>
public sealed record DebugLogPart(string Text, bool Expression);

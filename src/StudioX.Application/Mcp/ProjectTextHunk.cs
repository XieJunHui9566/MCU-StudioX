namespace StudioX.Application.Mcp;

/// <summary>针对原文件中唯一匹配的文本块做替换；所有匹配均按审批前的原文件定位。</summary>
public sealed record ProjectTextHunk(
    [property: System.Text.Json.Serialization.JsonPropertyName("oldText")] string OldText,
    [property: System.Text.Json.Serialization.JsonPropertyName("newText")] string NewText);

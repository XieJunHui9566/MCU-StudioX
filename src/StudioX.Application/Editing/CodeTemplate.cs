namespace StudioX.Application.Editing;

/// <summary>模板只保存文本，不执行脚本；语言适用范围与器件、开发环境组件版本无关。</summary>
public sealed record CodeTemplate(string Id, string Name, string Shortcut, string Language, string Description, string Body);

namespace StudioX.Application;

/// <summary>编辑快照绑定工程目录，切换工程后不能把旧文档保存到另一个工程。</summary>
public sealed record PortableDocument(string ProjectDirectory, SourceDocument Source);

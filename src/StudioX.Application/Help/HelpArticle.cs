namespace StudioX.Application.Help;

/// <summary>离线用户手册中的一个完整主题，内容与工作台视图无关。</summary>
public sealed record HelpArticle(string Id, string Category, string Title, string Summary,
    string[] Keywords, string[] Related, string Markdown);

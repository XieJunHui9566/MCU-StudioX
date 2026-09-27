namespace StudioX.Application;

/// <summary>仅支持 github.com；不要从任意 Git 远端推断 API 主机。</summary>
public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
    public Uri WebUrl => new($"https://github.com/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}");
}

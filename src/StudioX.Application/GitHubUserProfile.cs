namespace StudioX.Application;

/// <summary>已授权 GitHub.com 账号的显示资料。头像缺失不影响登录名展示。</summary>
public sealed record GitHubUserProfile(string Login, string? Name, byte[]? AvatarBytes);

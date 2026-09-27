namespace StudioX.Engine;

/// <summary>仅表示此仓库的 local config；为空时 Git 可能仍有用户的全局身份。</summary>
public sealed record GitIdentity(string? Name, string? Email);

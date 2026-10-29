namespace StudioX.Application.Mcp;

/// <summary>用户显式添加的 MCP 服务；JSON 不含密钥，凭据按配置身份单独保存。</summary>
public sealed record AiMcpServer(string Id, string Name, string Transport = "http", string Endpoint = "",
    string Command = "", string[]? Arguments = null, bool Enabled = true);

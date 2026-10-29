namespace StudioX.Application.Mcp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using StudioX.Foundation;

public sealed class AiMcpSettingsService(string dataDirectory)
{
    private string SettingsPath => Path.Combine(dataDirectory, "ai-mcp.json");
    public async Task<AiMcpServer[]> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return [];
        }
        if (new FileInfo(SettingsPath).Length > 128 * 1024)
        {
            throw new StudioXException("MCP_SETTINGS", "MCP 配置过大。");
        }
        var servers = await JsonStore.ReadAsync<AiMcpServer[]>(SettingsPath, token);
        Validate(servers);
        return servers;
    }

    public Task SaveAsync(AiMcpServer[] servers, CancellationToken token = default)
    {
        Validate(servers);
        return JsonStore.WriteAsync(SettingsPath, servers, token);
    }

    public static void Validate(AiMcpServer[] servers)
    {
        if (servers is null || servers.Length > 24 || servers.Any(server => server is null) ||
            servers.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != servers.Length)
        {
            throw new StudioXException("MCP_SETTINGS", "最多添加 24 个 MCP，标识不能重复。");
        }
        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 64 ||
                server.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
                string.IsNullOrWhiteSpace(server.Name) || server.Name.Length > 80 || server.Name.Any(char.IsControl) ||
                server.Arguments is { Length: > 32 } || server.Arguments?.Any(arg => arg is null || arg.Length > 2048 || arg.Any(char.IsControl)) == true)
            {
                throw new StudioXException("MCP_SETTINGS", "MCP 名称、标识或参数无效。");
            }
            if (server.Transport is "http" or "sse")
            {
                if (server.Endpoint is null || server.Endpoint.Length > 2048 || !Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var uri) ||
                    !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
                    uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                {
                    throw new StudioXException("MCP_ENDPOINT", "MCP 使用 HTTPS 地址；本机可用 HTTP。不在 URL 中放密钥或查询参数。");
                }
            }
            else if (server.Transport != "stdio" || !Path.IsPathFullyQualified(server.Command) ||
                !server.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MCP_COMMAND", "stdio 需要明确的本地 .exe 绝对路径；参数逐行填写，不通过 shell 执行。");
            }
        }
    }

    internal static string ConnectionIdentity(AiMcpServer server) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(server with
        {
            Enabled = true,
            Name = "",
            Arguments = server.Arguments ?? []
        }))));
    private static AiCredentialStore Credentials(AiMcpServer server) => new("MCUStudioX:MCP:" + ConnectionIdentity(server));
    public static bool HasSameConnection(AiMcpServer first, AiMcpServer second) =>
        ConnectionIdentity(first) == ConnectionIdentity(second);
    public bool HasToken(AiMcpServer server) => Credentials(server).HasApiKey();
    public void SetToken(AiMcpServer server, string token) => Credentials(server).SetApiKey(token);
    public void DeleteToken(AiMcpServer server) => Credentials(server).DeleteApiKey();

    public async Task<McpClient> ConnectAsync(AiMcpServer server, CancellationToken token, string? bearerOverride = null)
    {
        Validate([server]);
        IClientTransport transport;
        if (server.Transport == "stdio")
        {
            if (!File.Exists(server.Command))
            {
                throw new StudioXException("MCP_COMMAND", "MCP 可执行文件不存在。");
            }
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = server.Arguments ?? [],
                WorkingDirectory = Path.GetDirectoryName(server.Command),
                InheritEnvironmentVariables = false,
                EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
            });
        }
        else
        {
            var bearer = bearerOverride ?? Credentials(server).GetApiKey(new AiSettings().BaseUrl);
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new(server.Endpoint),
                EnableStandaloneGetStream = false,
                TransportMode = server.Transport == "sse" ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                AdditionalHeaders = string.IsNullOrEmpty(bearer) ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer }
            }, new HttpClient(new HttpClientHandler
            {
                // 凭据只交给用户选定的端点；跳转需要重新配置并授权连接。
                AllowAutoRedirect = false,
                UseCookies = false,
                CheckCertificateRevocationList = true
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            }, ownsHttpClient: true);
        }
        // 禁止服务端回调采样或任意本地资源；仅开放显式工具调用。
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            if (transport is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }
}

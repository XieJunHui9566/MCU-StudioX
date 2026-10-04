using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application.Espressif;

// 默认只使用隔离缓存、伪 HTTP 与 loopback，不读取用户授权或访问外部网络。
var output = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)) ?? "artifacts/validation/espressif-docs-service-current");
Directory.CreateDirectory(output);
var fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N"));
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException("验收失败：" + name);
    }
    checks.Add(name);
}

var server = new FakeServer();
var callbacks = new List<Task>();
Task Browser(Uri uri, CancellationToken token, string? wrongState = null, string? wrongIssuer = null, bool deny = false)
{
    var parameters = FakeServer.Query(uri.Query);
    Check(uri.Host == "mcp.espressif.com" && uri.AbsolutePath == "/docs/authorize", "浏览器仅接收官方授权端点");
    Check(parameters["code_challenge_method"] == "S256", "授权请求采用 PKCE S256");
    server.Challenge = parameters["code_challenge"];
    var redirect = new Uri(parameters["redirect_uri"]);
    Check(redirect.IsLoopback && redirect.Scheme == "http", "回调仅监听 loopback");
    var callback = new Uri(redirect + (deny ? "?error=access_denied&state=" : "?code=fake-authorization-code&state=") +
        Uri.EscapeDataString(wrongState ?? parameters["state"]) + "&iss=" + Uri.EscapeDataString(wrongIssuer ?? "https://mcp.espressif.com/docs"));
    callbacks.Add(Task.Run(async () =>
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(callback, token);
        Check(response.Headers.CacheControl?.NoStore == true, "浏览器回调禁止缓存");
    }, token));
    return Task.CompletedTask;
}

await using (var service = new EspressifDocumentationService(fixture, server))
{
    Check(!service.GetStatus().AuthenticationSaved && server.Requests == 0, "未授权状态无需联网");
    var denied = await service.SearchAsync("ESP-IDF GPIO output");
    Check(JsonDocument.Parse(denied).RootElement.GetProperty("status").GetString() == "authentication_required" && server.Requests == 0,
        "未授权查询不触发浏览器或联网");
    foreach (var privateQuery in new[] { "void main() {\n}", "int main(void);", "/home/private/main.c", "sk-offline-sensitive-token-for-validation", "API key=private-sentinel", "Bearer private-sentinel-auth-token", "C:\\Users\\private\\main.c", "file:///private/main.c" })
    {
        Check((await service.SearchAsync(privateQuery)).Contains("invalid_query", StringComparison.Ordinal) && server.Requests == 0, "明显私密或源码查询在 HTTP 前拒绝");
    }
    Check((await service.SearchAsync("ESP-IDF API key and password storage")).Contains("authentication_required", StringComparison.Ordinal), "公开安全文档术语允许查询");
    var connected = await service.ConnectAsync((uri, token) => Browser(uri, token));
    await Task.WhenAll(callbacks);
    if (connected.Status != "connected")
    {
        var diagnostic = new EspressifProtectedStore(fixture).Read<JsonElement>("diagnostic.dat");
        var raw = diagnostic.TryGetProperty("raw", out var detail) ? detail.GetString() ?? "" : "";
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"https?://\S+", "[URI redacted]");
        raw = raw.Replace(FakeServer.AccessToken, "[access redacted]", StringComparison.Ordinal).Replace(FakeServer.ClientSecret, "[secret redacted]", StringComparison.Ordinal);
        Console.WriteLine(raw);
    }
    Check(connected.Status == "connected" && connected.AuthenticationSaved, "完整 DCR 和授权流程成功");
    Check(server.Registrations == 1 && server.TokenExchanges == 1 && server.PkceVerified, "令牌交换携带正确注册凭据及 PKCE verifier");
    var result = await service.SearchAsync("ESP-IDF GPIO output", "zh");
    using var json = JsonDocument.Parse(result);
    Check(json.RootElement.GetProperty("status").GetString() == "ok", "查询结果来自 MCP 调用");
    Check(json.RootElement.GetProperty("language").GetString() == "Chinese" && server.LastLanguage == "Chinese", "按真实工具 enum 映射语言");
    Check(json.RootElement.GetProperty("citations").GetArrayLength() == 1, "仅提取官方 HTTPS 来源");
    Check(json.RootElement.GetProperty("versionNote").GetString()!.Contains("5.5.4", StringComparison.Ordinal), "提示 latest 索引和锁定 SDK 的区别");
    Check(!result.Contains(FakeServer.AccessToken, StringComparison.Ordinal) && !result.Contains(FakeServer.ClientSecret, StringComparison.Ordinal), "模型结果移除令牌和注册秘密");
    var retrievedAt = json.RootElement.GetProperty("retrievedAtUtc").GetString();
    var cached = await service.SearchAsync("ESP-IDF GPIO output", "Chinese");
    using var cachedJson = JsonDocument.Parse(cached);
    Check(server.Searches == 1 && cachedJson.RootElement.GetProperty("fromCache").GetBoolean() &&
        cachedJson.RootElement.GetProperty("retrievedAtUtc").GetString() == retrievedAt, "缓存保留真实联网时间");
    Check(Directory.GetFiles(Path.Combine(fixture, "secure", "espressif-docs")).All(path =>
        !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(FakeServer.ClientSecret, StringComparison.Ordinal)), "凭据只以 DPAPI 密文落盘");
    var token = new EspressifProtectedStore(fixture).Read<ModelContextProtocol.Authentication.TokenContainer>("oauth.dat")!;
    Check(token.ClientId == "offline-docs-client" && token.ClientSecret == FakeServer.ClientSecret && token.AuthorizationServer == "https://mcp.espressif.com/docs",
        "SDK 保存 DCR 身份及 issuer 以供跨重启刷新");
}

var secondServer = new FakeServer();
await using (var second = new EspressifDocumentationService(fixture, secondServer))
{
    Check(second.GetStatus().Status == "authorization_saved" && secondServer.Requests == 0, "重启状态准确显示已保存授权而不联网");
    var cached = await second.SearchAsync("ESP-IDF GPIO output", "zh");
    Check(secondServer.Requests == 0 && JsonDocument.Parse(cached).RootElement.GetProperty("fromCache").GetBoolean(), "内外宿主共享安全文档缓存");
    var store = new EspressifProtectedStore(fixture);
    store.BeginOperation();
    var saved = (await store.GetTokensAsync())!;
    saved.ObtainedAt = DateTimeOffset.UtcNow.AddDays(-1);
    saved.ExpiresIn = 1;
    await store.StoreTokensAsync(saved);
    var fresh = await second.SearchAsync("ESP-IDF SPI output", "en");
    Check(JsonDocument.Parse(fresh).RootElement.GetProperty("status").GetString() == "ok" && secondServer.Refreshes == 1 && secondServer.Registrations == 0,
        "重启后使用保存的 DCR 凭据刷新而不弹浏览器");
    secondServer.LimitSearch = true;
    var limited = await second.SearchAsync("ESP-IDF UART output");
    using var limit = JsonDocument.Parse(limited);
    Check(limit.RootElement.GetProperty("status").GetString() == "rate_limited" && limit.RootElement.GetProperty("httpStatus").GetInt32() == 429 &&
        limit.RootElement.GetProperty("retryAfter").GetString() == "120", "429 及 Retry-After 保留官方含义");
    Check(!limited.Contains(FakeServer.AccessToken, StringComparison.Ordinal), "公开错误不泄漏原始 OAuth 诊断");
    await second.DisconnectAsync();
    Check(!second.GetStatus().AuthenticationSaved && Directory.GetFiles(Path.Combine(fixture, "secure", "espressif-docs")).All(x => Path.GetFileName(x) == "generation.dat"), "断开清理服务私有缓存且保留撤销屏障");
}

var raceFixture = Path.Combine(output, "race-" + Guid.NewGuid().ToString("N"));
var staleWriter = new EspressifProtectedStore(raceFixture);
staleWriter.BeginOperation();
var revoker = new EspressifProtectedStore(raceFixture);
revoker.Clear();
var staleWriteRejected = false;
try
{
    await staleWriter.StoreTokensAsync(new ModelContextProtocol.Authentication.TokenContainer { TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow, AccessToken = "fake-stale-refresh" });
}
catch (EspressifAuthorizationRevokedException)
{
    staleWriteRejected = true;
}
Check(staleWriteRejected && revoker.Read<ModelContextProtocol.Authentication.TokenContainer>("oauth.dat") is null, "跨宿主撤销阻止旧刷新复活授权");
staleWriter.BeginOperation();
await staleWriter.StoreTokensAsync(new ModelContextProtocol.Authentication.TokenContainer { TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow, AccessToken = "fake-new-authorization" });
Check(revoker.Read<ModelContextProtocol.Authentication.TokenContainer>("oauth.dat")?.AccessToken == "fake-new-authorization", "新显式操作可在撤销后保存新授权");
revoker.BeginOperation();
revoker.StoreNewAuthorization(new ModelContextProtocol.Authentication.TokenContainer
{
    TokenType = "Bearer",
    ObtainedAt = DateTimeOffset.UtcNow,
    AccessToken = "fake-newer-authorization",
    ClientId = "fake-newer-client"
});
var oldRefreshRejected = false;
try
{
    await staleWriter.StoreTokensAsync(new ModelContextProtocol.Authentication.TokenContainer
    {
        TokenType = "Bearer",
        ObtainedAt = DateTimeOffset.UtcNow,
        AccessToken = "fake-old-refresh",
        ClientId = "fake-old-client"
    });
}
catch (EspressifAuthorizationRevokedException)
{
    oldRefreshRejected = true;
}
Check(oldRefreshRejected && revoker.Read<ModelContextProtocol.Authentication.TokenContainer>("oauth.dat")?.ClientId == "fake-newer-client",
    "新显式授权提交后旧宿主刷新不能覆盖新 DCR 身份");
staleWriter.BeginOperation();
var largeResults = Enumerable.Range(0, 64).Select(x => new string('中', 24000) + x).ToList();
staleWriter.WriteResults(largeResults);
Check(new FileInfo(Path.Combine(raceFixture, "secure", "espressif-docs", "results.dat")).Length < 4 * 1024 * 1024 &&
    staleWriter.ReadForOperation<List<string>>("results.dat") is { Count: > 0 }, "中文大结果缓存按字节淘汰且能重新读取");

foreach (var mismatch in new[] { "state", "issuer", "denied" })
{
    var isolated = Path.Combine(output, mismatch + "-" + Guid.NewGuid().ToString("N"));
    var invalidServer = new FakeServer();
    await using var invalid = new EspressifDocumentationService(isolated, invalidServer);
    var status = await invalid.ConnectAsync((uri, token) => Browser(uri, token,
        wrongState: mismatch == "state" ? "incorrect-state" : null,
        wrongIssuer: mismatch == "issuer" ? "https://invalid.example" : null, deny: mismatch == "denied"));
    Check(status.Status != "connected" && !status.AuthenticationSaved && invalidServer.TokenExchanges == 0, "拒绝不合法或拒绝的授权响应：" + mismatch);
    if (mismatch == "denied")
    {
        Check(status.Status == "authorization_denied", "授权拒绝有明确安全状态");
    }
}

var cancelledServer = new FakeServer();
await using (var cancelled = new EspressifDocumentationService(Path.Combine(output, "cancelled-" + Guid.NewGuid().ToString("N")), cancelledServer))
{
    using var cancellation = new CancellationTokenSource();
    var status = await cancelled.ConnectAsync((_, token) =>
    {
        cancellation.Cancel();
        return Task.FromCanceled(token);
    }, cancellation.Token);
    Check(status.Status == "cancelled" && cancelledServer.TokenExchanges == 0, "取消授权关闭 loopback 并停止令牌交换");
}

var hotFixture = Path.Combine(output, "hot-client-" + Guid.NewGuid().ToString("N"));
var hotServer = new FakeServer();
await using (var hot = new EspressifDocumentationService(hotFixture, hotServer))
{
    var browserCalls = 0;
    var connected = await hot.ConnectAsync((uri, token) =>
    {
        browserCalls++;
        hotServer.Challenge = FakeServer.Query(uri.Query)["code_challenge"];
        return Browser(uri, token);
    });
    Check(connected.Status == "connected", "热客户端授权夹具已连接");
    var store = new EspressifProtectedStore(hotFixture);
    store.BeginOperation();
    var saved = (await store.GetTokensAsync())!;
    saved.ObtainedAt = DateTimeOffset.UtcNow.AddDays(-1);
    saved.ExpiresIn = 1;
    await store.StoreTokensAsync(saved);
    hotServer.RejectRefresh = true;
    var response = await hot.SearchAsync("ESP-IDF hot client expired authorization");
    Check(JsonDocument.Parse(response).RootElement.GetProperty("status").GetString() == "authentication_required" &&
        browserCalls == 1 && hotServer.Refreshes > 0, "已连接客户端刷新失败返回需授权且不会自动打开浏览器");
    await hot.DisconnectAsync();
    Check(browserCalls == 1, "释放热客户端也不会打开浏览器");
}

if (args.Contains("--probe-official", StringComparer.Ordinal))
{
    var officialFixture = Path.Combine(output, "official-probe-" + Guid.NewGuid().ToString("N"));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await using var official = new EspressifDocumentationService(officialFixture);
    var reached = false;
    var status = await official.ConnectAsync((uri, token) =>
    {
        reached = uri.Scheme == "https" && uri.Host == "mcp.espressif.com" && uri.AbsolutePath == "/docs/authorize" &&
            FakeServer.Query(uri.Query).GetValueOrDefault("code_challenge_method") == "S256";
        cancellation.Cancel();
        return Task.CompletedTask;
    }, cancellation.Token);
    Check(reached && status.Status == "cancelled" && !status.AuthenticationSaved, "真实官方元数据及 DCR 已到达 PKCE 授权地址，未打开浏览器或登录");
    await official.DisconnectAsync();
}

File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { success = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Espressif docs validation: {checks.Count} checks passed.");

internal sealed class FakeServer : HttpMessageHandler
{
    public const string AccessToken = "offline-docs-access-token";
    public const string ClientSecret = "offline-docs-client-secret";
    public string? Challenge
    {
        get; set;
    }
    public int Requests
    {
        get; private set;
    }
    public int Registrations
    {
        get; private set;
    }
    public int TokenExchanges
    {
        get; private set;
    }
    public int Refreshes
    {
        get; private set;
    }
    public int Searches
    {
        get; private set;
    }
    public bool PkceVerified
    {
        get; private set;
    }
    public bool LimitSearch
    {
        get; set;
    }
    public bool RejectRefresh
    {
        get; set;
    }
    public string? LastLanguage
    {
        get; private set;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/oauth-protected-resource/docs")
        {
            return Json(new
            {
                resource = "https://mcp.espressif.com/docs",
                authorization_servers = new[] { "https://mcp.espressif.com/docs" },
                scopes_supported = new[] { "read:user" }
            });
        }
        if (path == "/.well-known/oauth-authorization-server/docs")
        {
            return Json(new
            {
                issuer = "https://mcp.espressif.com/docs",
                authorization_endpoint = "https://mcp.espressif.com/docs/authorize",
                token_endpoint = "https://mcp.espressif.com/docs/token",
                registration_endpoint = "https://mcp.espressif.com/docs/register",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
                code_challenge_methods_supported = new[] { "S256" },
                scopes_supported = new[] { "read:user" },
                authorization_response_iss_parameter_supported = true
            });
        }
        if (path == "/docs/register")
        {
            Registrations++;
            return Json(new
            {
                client_id = "offline-docs-client",
                client_secret = ClientSecret,
                token_endpoint_auth_method = "client_secret_post"
            });
        }
        if (path == "/docs/token")
        {
            var form = Query(await request.Content!.ReadAsStringAsync(token));
            if (form["grant_type"] == "refresh_token")
            {
                Refreshes++;
                if (RejectRefresh)
                {
                    var rejected = Json(new
                    {
                        error = "invalid_grant",
                        error_description = "offline token revoked"
                    });
                    rejected.StatusCode = HttpStatusCode.BadRequest;
                    return rejected;
                }
            }
            else
            {
                TokenExchanges++;
                var verifier = form["code_verifier"];
                var digest = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                PkceVerified = digest == Challenge;
            }
            if (form.GetValueOrDefault("client_secret") != ClientSecret)
            {
                throw new InvalidOperationException("测试令牌交换未使用注册凭据。");
            }
            return Json(new
            {
                access_token = AccessToken,
                refresh_token = "offline-docs-refresh-token",
                token_type = "Bearer",
                expires_in = 3600,
                scope = "read:user"
            });
        }
        if (path != "/docs")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
        }
        if (request.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (request.Headers.Authorization?.Parameter != AccessToken)
        {
            var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("authorization required") };
            unauthorized.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer resource_metadata=\"https://mcp.espressif.com/.well-known/oauth-protected-resource/docs\"");
            return unauthorized;
        }
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var root = document.RootElement;
        var method = root.GetProperty("method").GetString();
        if (!root.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        if (method is not ("initialize" or "tools/list" or "tools/call"))
        {
            return Json(new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                error = new
                {
                    code = -32601,
                    message = "Method not found"
                }
            });
        }
        object result = method switch
        {
            "initialize" => new
            {
                protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                capabilities = new
                {
                    tools = new
                    {
                    }
                },
                serverInfo = new
                {
                    name = "offline Espressif",
                    version = "1"
                }
            },
            "tools/list" => new
            {
                tools = new[]
                {
                    new
                    {
                        name = "search_espressif_sources", description = "offline official docs",
                        inputSchema = new
                        {
                            type = "object", properties = new
                            {
                                query = new { type = "string" }, language = new { type = "string", @enum = new[] { "Chinese", "English" } }
                            }, required = new[] { "query", "language" }
                        }
                    }
                }
            },
            "tools/call" => Search(root),
            _ => new { }
        };
        if (method == "tools/call" && LimitSearch)
        {
            var limit = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("quota reached") };
            limit.Headers.TryAddWithoutValidation("Retry-After", "120");
            return limit;
        }
        return Json(new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            result
        });
    }

    private object Search(JsonElement root)
    {
        Searches++;
        LastLanguage = root.GetProperty("params").GetProperty("arguments").GetProperty("language").GetString();
        return new
        {
            content = new[]
            {
                new { type = "text", text = "GPIO docs https://docs.espressif.com/projects/esp-idf/en/latest/esp32/api-reference/peripherals/gpio.html\n" +
                    "Unsafe https://invalid.example/docs\n" + AccessToken + " client_secret=" + ClientSecret }
            },
            isError = false
        };
    }

    public static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length == 2 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "");

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
}

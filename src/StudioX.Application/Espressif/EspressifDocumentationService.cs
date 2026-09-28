namespace StudioX.Application.Espressif;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>拥有乐鑫官方文档 MCP 连接、显式授权和受保护缓存，不接触工程源码或设备会话。</summary>
public sealed class EspressifDocumentationService : IAsyncDisposable
{
    public static Uri Endpoint { get; } = new("https://mcp.espressif.com/docs");
    private const string ToolName = "search_espressif_sources";
    private readonly EspressifProtectedStore store;
    private readonly EspressifDocumentationHttpPolicy policy;
    private readonly HttpClient http;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object statusLock = new();
    private EspressifDocumentationStatus status = new("authentication_required", false, null,
        "请显式连接乐鑫官方文档 MCP。", Endpoint.AbsoluteUri, null);
    private McpClient? client;
    private HttpClientTransport? transport;
    private JsonElement? inputSchema;
    private string? clientGeneration;
    private CancellationTokenSource? activeOperation;
    private bool disposed;
    private static readonly Regex PrivateQuery = new(
        @"(?i)(\b(?:sk|ghp|gho|github_pat)[-_][A-Za-z0-9_-]{12,}|\bbearer\s+\S{12,}|\b(?:access_token|refresh_token|api[_ -]?key|client_secret|password)\s*[:=]\s*\S+|[A-Za-z]:[\\/]|file://|(?:^|\s)\\\\\S+|(?:^|\s)/(?:home|Users|root|tmp|private|mnt|etc|var|opt)/\S+|^\s*(?:static\s+|const\s+|volatile\s+)*(?:void|int|char|float|double|unsigned|uint\d+_t)\s+.+[;=]|[A-Za-z0-9_-]{64,})",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>HTTP 处理器仅用于注入离线测试；服务拥有传入处理器及其创建的客户端。</summary>
    public EspressifDocumentationService(string dataDirectory, HttpMessageHandler? httpHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        store = new EspressifProtectedStore(dataDirectory);
        policy = new EspressifDocumentationHttpPolicy(httpHandler ?? new HttpClientHandler { AllowAutoRedirect = false });
        http = new HttpClient(policy) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MCU-StudioX/1.0");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public EspressifDocumentationStatus GetStatus()
    {
        lock (statusLock)
        {
            if (disposed)
            {
                return status with { Status = "disposed", Operation = null };
            }
            try
            {
                var saved = HasSavedTokens();
                if (!saved && status.Status is "connected" or "authorization_saved")
                {
                    return status with { Status = "authentication_required", AuthenticationSaved = false, Message = "乐鑫文档授权已撤销，请显式重新连接。" };
                }
                return saved && status.Status == "authentication_required" && status.LastErrorCode is null
                    ? status with { Status = "authorization_saved", AuthenticationSaved = true, Message = "已安全保存乐鑫授权，查询时将按需连接。" }
                    : status with { AuthenticationSaved = saved };
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return status with
                {
                    Status = "storage_error",
                    AuthenticationSaved = false,
                    Message = "无法读取乐鑫授权安全缓存，请断开后重新授权。",
                    LastErrorCode = "ESPRESSIF_AUTH_STORAGE"
                };
            }
        }
    }

    /// <summary>只有调用本方法才允许打开外部浏览器；OAuth 的 PKCE、state、iss 和刷新由 MCP SDK 校验。</summary>
    public async Task<EspressifDocumentationStatus> ConnectAsync(Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(5));
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeOperation = linked;
            store.BeginOperation(linked.Token);
            SetStatus("connecting", "connect", "正在连接乐鑫官方文档服务。", null);
            policy.ResetFailure();
            await CloseClientAsync().ConfigureAwait(false);
            using var callback = new EspressifOAuthCallback();
            // 动态注册的回调 URI 与授权事务绑定；显式重新连接使用新端口时重新注册。
            // 先前令牌保留到新授权成功，取消连接不会删除仍可使用的授权。
            var authorizationActive = 1;
            try
            {
                await CreateClientAsync(callback.RedirectUri, (context, token) =>
                    Volatile.Read(ref authorizationActive) == 1
                        ? callback.ReceiveAsync(context, openBrowser, token)
                        : Task.FromException<AuthorizationResult?>(new EspressifAuthenticationRequiredException()),
                    linked.Token, interactive: true).ConfigureAwait(false);
            }
            finally
            {
                // 已建立的客户端继续复用；查询和释放资源不能重新打开授权浏览器。
                Interlocked.Exchange(ref authorizationActive, 0);
            }
            clientGeneration = store.OperationGeneration;
            SetStatus("connected", null, "已连接乐鑫官方文档 MCP。", null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await CloseClientAsync().ConfigureAwait(false);
            SetStatus("cancelled", null, "乐鑫授权已取消或等待超时。", "ESPRESSIF_AUTH_CANCELLED");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SaveDiagnostic(error);
            var failure = Classify(error);
            await CloseClientAsync().ConfigureAwait(false);
            SetStatus(failure.Status, null, failure.Message, failure.Code);
        }
        finally
        {
            activeOperation = null;
            gate.Release();
        }
        return GetStatus();
    }

    /// <summary>取消在途调用并移除本服务的当前用户授权及文档缓存，不影响模型账户或其它厂商凭据。</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var active = activeOperation;
        if (active is not null)
        {
            try
            {
                await active.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 被观察的操作已退出并释放自己的取消源，继续断开当前连接。
            }
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await CloseClientAsync().ConfigureAwait(false);
            store.Clear(cancellationToken);
            SetStatus("authentication_required", null, "已移除乐鑫授权及本地文档缓存。", null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>只接收单行资料查询；未授权或授权过期时返回 authentication_required，查询不会打开浏览器。</summary>
    public async Task<string> SearchAsync(string query, string language = "zh", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 384 ||
            query.Any(c => char.IsControl(c) || c is '{' or '}' or '`') || PrivateQuery.IsMatch(query))
        {
            return EspressifDocumentationReply.Failure("invalid_query", "请输入不超过 384 字符的单行公开资料查询，不要发送源码、凭据或本机路径。");
        }
        if (string.IsNullOrWhiteSpace(language) || language.Length > 32 || language.Any(char.IsControl))
        {
            return EspressifDocumentationReply.Failure("invalid_language", "语言参数无效。");
        }
        query = query.Trim();
        language = NormalizeLanguage(language.Trim());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token, timeout.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeOperation = linked;
            if (!HasSavedTokens())
            {
                await CloseClientAsync().ConfigureAwait(false);
                SetStatus("authentication_required", null, "请在设置中显式连接乐鑫官方文档 MCP。", null);
                return EspressifDocumentationReply.Failure("authentication_required", status.Message);
            }
            var generation = store.BeginOperation(linked.Token);
            if (client is not null && clientGeneration != generation)
            {
                await CloseClientAsync().ConfigureAwait(false);
            }
            clientGeneration = generation;
            var cache = store.ReadForOperation<List<CacheEntry>>("results.dat", linked.Token) ?? [];
            var cached = cache.FirstOrDefault(x => x.Query == query && x.Language == language &&
                timeProvider.GetUtcNow() - x.RetrievedAt < TimeSpan.FromHours(12));
            if (cached is not null)
            {
                // 缓存保留原始联网时间；跨内置和外部宿主复用时不伪造新检索。
                var json = JsonNode.Parse(cached.Json)!.AsObject();
                json["fromCache"] = true;
                return json.ToJsonString();
            }
            SetStatus("searching", "search", "正在查询乐鑫官方文档。", null);
            policy.ResetFailure();
            if (client is null)
            {
                await CreateClientAsync(new Uri("http://127.0.0.1:49152/studiox-espressif-inactive/"),
                    (_, _) => Task.FromException<AuthorizationResult?>(new EspressifAuthenticationRequiredException()), linked.Token).ConfigureAwait(false);
            }
            var arguments = Arguments(query, language);
            var currentClient = client ?? throw new InvalidOperationException("乐鑫文档客户端尚未初始化。");
            var result = await currentClient.CallToolAsync(ToolName, arguments, cancellationToken: linked.Token).ConfigureAwait(false);
            var retrievedAt = timeProvider.GetUtcNow();
            var tokens = await store.GetTokensAsync(linked.Token).ConfigureAwait(false);
            var response = EspressifDocumentationReply.Result(result, query,
                arguments.TryGetValue("language", out var actualLanguage) ? actualLanguage?.ToString() ?? "server_default" : "server_default", retrievedAt, tokens);
            if (result.IsError != true)
            {
                // 这里只限制缓存占用；不限制 Agent 调用轮数，不伪造官方额度。
                cache.RemoveAll(x => x.Query == query && x.Language == language || retrievedAt - x.RetrievedAt >= TimeSpan.FromHours(12));
                cache.Add(new CacheEntry(query, language, retrievedAt, response));
                store.WriteResults(cache.TakeLast(64).ToList(), linked.Token);
            }
            SetStatus("connected", null, result.IsError == true ? "官方服务返回查询错误，请查看工具结果。" : "已完成乐鑫文档查询。", null);
            return response;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await CloseClientAsync().ConfigureAwait(false);
            if (!timeout.IsCancellationRequested)
            {
                SetStatus("cancelled", null, "乐鑫文档查询已取消。", "ESPRESSIF_QUERY_CANCELLED");
                throw;
            }
            SetStatus("timeout", null, "乐鑫文档查询等待超时。", "ESPRESSIF_QUERY_TIMEOUT");
            return EspressifDocumentationReply.Failure("timeout", status.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SaveDiagnostic(error);
            var failure = Classify(error);
            var httpStatus = policy.FailureStatusCode;
            var retryAfter = policy.RetryAfter;
            await CloseClientAsync().ConfigureAwait(false);
            SetStatus(failure.Status, null, failure.Message, failure.Code);
            return EspressifDocumentationReply.Failure(failure.Status, failure.Message, httpStatus, retryAfter, error.GetType().Name);
        }
        finally
        {
            activeOperation = null;
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (statusLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseClientAsync().ConfigureAwait(false);
            http.Dispose();
        }
        finally
        {
            gate.Release();
        }
        // 不释放 gate/lifetime，使已开始但仍在取消边界的调用可以安全结束。
    }

    private async Task CreateClientAsync(Uri redirectUri,
        Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>> callback, CancellationToken token, bool interactive = false)
    {
        var oauth = new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            TokenCache = interactive ? new InteractiveTokenCache(store) : store,
            Scopes = ["read:user"],
            ScopeSelector = scopes => scopes?.Where(x => x is "read:user" or "offline_access"),
            AuthServerSelector = servers => servers.SingleOrDefault(x => x == Endpoint),
            AuthorizationCallbackHandler = callback,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = "MCU StudioX",
                ClientUri = new Uri("https://github.com/XieJunHui9566/MCU-StudioX"),
                ApplicationType = "native"
            }
        };
        transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            ConnectionTimeout = interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30),
            MaxReconnectionAttempts = 0,
            OAuth = oauth,
            Name = "Espressif official documentation"
        }, http, ownsHttpClient: false);
        client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "MCU StudioX", Version = "1" },
            ProtocolVersion = "2025-06-18",
            InitializationTimeout = interactive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30)
        }, cancellationToken: token).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false);
        var tool = tools.SingleOrDefault(x => x.Name == ToolName) ??
            throw new InvalidDataException("官方服务未公布文档查询工具。");
        inputSchema = tool.ProtocolTool.InputSchema.Clone();
    }

    private Dictionary<string, object?> Arguments(string query, string language)
    {
        if (inputSchema is not { } schema || !schema.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("query", out _) || !properties.TryGetProperty("language", out var languageSchema))
        {
            throw new InvalidDataException("官方文档工具 schema 不兼容。");
        }
        var arguments = new Dictionary<string, object?> { ["query"] = query };
        if (languageSchema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var candidates = values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
            var selected = candidates.FirstOrDefault(x => NormalizeLanguage(x) == language) ??
                throw new InvalidDataException("官方文档工具不支持所选语言。");
            arguments["language"] = selected;
            return arguments;
        }
        // 精确枚举未公布时保留调用方原值；没有语言字段猜测或修改工具契约。
        arguments["language"] = language;
        return arguments;
    }

    private static string NormalizeLanguage(string language) => language.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "chinese" or "中文" => "zh",
        "en" or "en-us" or "english" or "英文" => "en",
        _ => language
    };

    private bool HasSavedTokens()
    {
        var tokens = store.Read<TokenContainer>("oauth.dat");
        return tokens is not null && (!string.IsNullOrWhiteSpace(tokens.AccessToken) || !string.IsNullOrWhiteSpace(tokens.RefreshToken)) &&
            tokens.AuthorizationServer == Endpoint.AbsoluteUri;
    }

    private void SetStatus(string state, string? operation, string message, string? code)
    {
        lock (statusLock)
        {
            status = new EspressifDocumentationStatus(state, status.AuthenticationSaved, operation, message, Endpoint.AbsoluteUri, code);
        }
    }

    private (string Status, string Message, string Code) Classify(Exception error)
    {
        if (ContainsAuthorizationDenied(error))
        {
            return ("authorization_denied", "已拒绝乐鑫文档授权。", "ESPRESSIF_AUTH_DENIED");
        }
        if (policy.FailureStatusCode == 429)
        {
            return ("rate_limited", "乐鑫官方服务达到调用额度，请按 Retry-After 等待后重试。", "ESPRESSIF_RATE_LIMITED");
        }
        if (policy.FailureStatusCode is 401 or 403 || ContainsAuthenticationRequired(error))
        {
            return ("authentication_required", "乐鑫文档授权不可用，请在设置中显式重新连接。", "ESPRESSIF_AUTH_REQUIRED");
        }
        return ("service_error", "乐鑫文档服务调用失败，安全诊断已保留。", "ESPRESSIF_DOCS_SERVICE");
    }

    private static bool ContainsAuthenticationRequired(Exception error) =>
        error is EspressifAuthenticationRequiredException or EspressifAuthorizationRevokedException || error.InnerException is { } inner && ContainsAuthenticationRequired(inner);

    private static bool ContainsAuthorizationDenied(Exception error) =>
        error is EspressifAuthorizationDeniedException || error.InnerException is { } inner && ContainsAuthorizationDenied(inner);

    private void SaveDiagnostic(Exception error)
    {
        try
        {
            var raw = error.ToString();
            store.Write("diagnostic.dat", new
            {
                observedAtUtc = timeProvider.GetUtcNow(),
                type = error.GetType().FullName,
                httpStatus = policy.FailureStatusCode,
                raw = raw.Length <= 128000 ? raw : raw[..128000]
            });
        }
        catch (Exception storageError) when (storageError is not OutOfMemoryException)
        {
            // 原始异常仍保留在本调用中；安全缓存不可写时不把敏感 OAuth 诊断退回明文日志。
            error.Data["ProtectedDiagnosticStorageFailure"] = storageError.GetType().Name;
        }
    }

    private async Task CloseClientAsync()
    {
        var previousClient = client;
        var previousTransport = transport;
        client = null;
        transport = null;
        inputSchema = null;
        clientGeneration = null;
        if (previousClient is not null)
        {
            try
            {
                await previousClient.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // 释放失败不阻止后续传输与宿主清理，原始工具诊断仍以当前用户密文保存。
                SaveDiagnostic(error);
            }
        }
        if (previousTransport is not null)
        {
            try
            {
                await previousTransport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                SaveDiagnostic(error);
            }
        }
    }

    private sealed record CacheEntry(string Query, string Language, DateTimeOffset RetrievedAt, string Json);

    private sealed class InteractiveTokenCache(EspressifProtectedStore store) : ITokenCache
    {
        private TokenContainer? current;

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default) =>
            current is null ? ValueTask.FromResult<TokenContainer?>(null) : store.GetTokensAsync(cancellationToken);

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
        {
            if (current is null)
            {
                store.StoreNewAuthorization(tokens, cancellationToken);
            }
            else
            {
                await store.StoreTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
            }
            current = tokens;
        }
    }
}

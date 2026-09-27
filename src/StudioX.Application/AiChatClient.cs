using System.Net.Http.Headers;
using System.Text.Json;
using StudioX.Foundation;

namespace StudioX.Application;

/// <summary>管理 Chat Completions 的 HTTP 生命周期；协议编解码与会话执行由各自组件负责。</summary>
public sealed class AiChatClient : IDisposable
{
    // 保留供 Agent 筛选临时图像的入口，与请求编码器使用同一份协议边界。
    internal const int MaximumInlineImageBytes = AiChatProtocolLimits.MaximumInlineImageBytes;
    internal const int MaximumInlineImageTotalBytes = AiChatProtocolLimits.MaximumInlineImageTotalBytes;

    private readonly Func<string, string?> apiKeyProvider;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public AiChatClient(AiCredentialStore credentials, HttpClient? client = null)
        : this((credentials ?? throw new ArgumentNullException(nameof(credentials))).GetApiKey, client)
    {
    }

    /// <summary>可注入凭据读取器与 HTTP 客户端；外部传入的客户端由调用方释放。</summary>
    public AiChatClient(Func<string, string?> apiKeyProvider, HttpClient? client = null)
    {
        this.apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        this.client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        ownsClient = client is null;
        if (ownsClient)
        {
            // 超时由每次请求单独管理，避免 HttpClient 的全局超时覆盖用户配置。
            this.client.Timeout = Timeout.InfiniteTimeSpan;
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    /// <summary>仅已核实支持视觉输入的官方模型可接收内置 Agent 的页面图像。</summary>
    public static bool SupportsInlineImages(AiSettings settings)
    {
        var endpoint = AiSettingsService.CompletionUri(settings);
        return AiModelCapabilities.SupportsInlineImages(settings.Model,
            AiModelCapabilities.IsOfficialDeepSeek(endpoint));
    }

    public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest conversation,
        CancellationToken token = default) =>
        SendAsync(settings, conversation, onUpdate: null, token);

    /// <summary>逐段报告模型输出，完成后返回可重放的完整响应；兼容完整 JSON 响应。</summary>
    public async Task<AiChatResponse> CompleteStreamingAsync(AiSettings settings, AiChatRequest conversation,
        Action<AiStreamUpdate> onUpdate, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        return await SendAsync(settings, conversation, onUpdate, token);
    }

    private async Task<AiChatResponse> SendAsync(AiSettings settings, AiChatRequest conversation,
        Action<AiStreamUpdate>? onUpdate, CancellationToken token)
    {
        AiSettingsService.Validate(settings);
        var endpoint = AiSettingsService.CompletionUri(settings);
        token.ThrowIfCancellationRequested();

        var key = ReadApiKey(settings.BaseUrl);
        var officialDeepSeek = AiModelCapabilities.IsOfficialDeepSeek(endpoint);
        var streaming = onUpdate is not null;
        var body = AiChatRequestWriter.Serialize(settings.Model, officialDeepSeek,
            settings.ReasoningEffort, conversation, streaming);
        using var request = CreateRequest(endpoint, key, body, streaming);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 兼容端点可能不实现 SSE；只在尚未收到模型输出时允许一次非流式重试。
                if (streaming && !officialDeepSeek && (int)response.StatusCode is 400 or 406 or 415 or 422 or 501)
                {
                    var fallback = await CompleteAsync(settings, conversation, linked.Token);
                    AiStreamUpdateReporter.Report(fallback, onUpdate!);
                    return fallback;
                }

                throw new StudioXException("AI_HTTP",
                    $"AI API 请求失败（HTTP {(int)response.StatusCode}）。请检查地址、模型、密钥和服务状态。");
            }

            if (!streaming || response.Content.Headers.ContentType?.MediaType is "application/json")
            {
                var complete = await AiResponseBodyReader.ReadAsync(response.Content, linked.Token);
                if (onUpdate is not null)
                {
                    AiStreamUpdateReporter.Report(complete, onUpdate);
                }

                return complete;
            }

            return await AiSseResponseReader.ReadAsync(response.Content, onUpdate!, linked.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StudioXException("AI_TIMEOUT", "AI API 请求超时。");
        }
        catch (HttpRequestException)
        {
            throw new StudioXException("AI_NETWORK", "无法连接 AI API，请检查网络和接口地址。");
        }
        catch (IOException)
        {
            throw new StudioXException("AI_NETWORK", "读取 AI API 响应失败。");
        }
        catch (JsonException)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 返回的 JSON 无效。");
        }
        catch (Exception error) when (error is not StudioXException and not OperationCanceledException and not OutOfMemoryException)
        {
            throw new StudioXException("AI_NETWORK", "AI API 请求失败，请检查网络和接口地址。");
        }
    }

    private string ReadApiKey(string baseUrl)
    {
        string? key;
        try
        {
            key = apiKeyProvider(baseUrl);
        }
        catch (Exception)
        {
            // 凭据提供器可能在异常中包含敏感值；对外保留稳定错误码，不暴露凭据详情。
            throw new StudioXException("AI_CREDENTIAL", "无法读取 AI API Key。");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Any(character => character is < '!' or > '~'))
        {
            throw new StudioXException("AI_API_KEY_REQUIRED", "请先设置 AI API Key。");
        }

        return key;
    }

    private static HttpRequestMessage CreateRequest(Uri endpoint, string key, byte[] body, bool streaming)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        catch (FormatException)
        {
            request.Dispose();
            throw new StudioXException("AI_API_KEY_REQUIRED", "保存的 AI API Key 格式无效，请重新设置。");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return request;
    }
}

namespace StudioX.Application.Espressif;

using System.Net;

/// <summary>限制 OAuth 和 MCP 请求至固定官方服务，拒绝重定向及过大正文。</summary>
internal sealed class EspressifDocumentationHttpPolicy(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public int? FailureStatusCode { get; private set; }
    public string? RetryAfter { get; private set; }

    public void ResetFailure()
    {
        FailureStatusCode = null;
        RetryAfter = null;
    }

    public static bool IsOfficial(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        uri.IdnHost.Equals("mcp.espressif.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.AbsolutePath is "/docs" or "/docs/authorize" or "/docs/token" or "/docs/register" or
            "/.well-known/oauth-protected-resource/docs" or "/.well-known/oauth-authorization-server/docs" or
            "/.well-known/oauth-protected-resource" or "/.well-known/oauth-authorization-server" or
            "/.well-known/openid-configuration/docs" or "/docs/.well-known/openid-configuration");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !IsOfficial(request.RequestUri))
        {
            throw new InvalidOperationException("乐鑫文档服务拒绝非预期端点。");
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 ||
            response.RequestMessage?.RequestUri is { } actual && !IsOfficial(actual))
        {
            response.Dispose();
            throw new InvalidOperationException("乐鑫文档服务拒绝 HTTP 重定向。");
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            FailureStatusCode = (int)response.StatusCode;
            var retry = response.Headers.RetryAfter?.ToString();
            RetryAfter = retry is { Length: <= 128 } ? retry : null;
        }
        try
        {
            // 文档工具无需无限 SSE 推送；POST 正文有界，避免异常服务器耗尽宿主内存。
            await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

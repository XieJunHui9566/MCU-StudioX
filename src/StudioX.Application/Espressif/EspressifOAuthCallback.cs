namespace StudioX.Application.Espressif;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Authentication;

/// <summary>拥有一次显式授权的 loopback 接收器，将响应的 state 和 iss 原样交给 SDK 校验。</summary>
internal sealed class EspressifOAuthCallback : IDisposable
{
    private readonly HttpListener listener = new();
    public Uri RedirectUri { get; }

    public EspressifOAuthCallback()
    {
        // 使用操作系统分配端口；只监听 IPv4 loopback，路径随机且不记录回调 URI。
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        RedirectUri = new Uri($"http://127.0.0.1:{port}/studiox-espressif-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}/");
        listener.Prefixes.Add(RedirectUri.AbsoluteUri);
        listener.Start();
    }

    public async Task<AuthorizationResult?> ReceiveAsync(AuthorizationCallbackContext authorization,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken)
    {
        if (!EspressifDocumentationHttpPolicy.IsOfficial(authorization.AuthorizationUri) ||
            authorization.AuthorizationUri.AbsolutePath != "/docs/authorize" || authorization.RedirectUri != RedirectUri)
        {
            throw new InvalidOperationException("乐鑫授权元数据不匹配。");
        }
        await openBrowser(authorization.AuthorizationUri, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = context.Request;
            if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != RedirectUri.AbsolutePath ||
                request.RemoteEndPoint is not { Address: var remote } || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }
            var query = request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var issuer = query["iss"];
            var denied = query["error"] == "access_denied";
            var expectedState = authorization.AuthorizationUri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('=', 2))
                .Where(pair => pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == "state")
                .Select(pair => Uri.UnescapeDataString(pair[1]))
                .SingleOrDefault();
            // OAuth 错误响应没有可交换的 code，不能交给 SDK 成功分支；仍需绑定同一事务。
            var validDenial = denied && query.GetValues("error")?.Length == 1 && query.GetValues("state")?.Length == 1 &&
                expectedState is not null && string.Equals(state, expectedState, StringComparison.Ordinal) &&
                (issuer is null || query.GetValues("iss")?.Length == 1 && issuer == EspressifDocumentationService.Endpoint.AbsoluteUri);
            var valid = query.GetValues("code")?.Length == 1 && query.GetValues("state")?.Length == 1 &&
                code is { Length: > 0 and <= 8192 } && state is { Length: > 0 and <= 1024 } &&
                (issuer is null || query.GetValues("iss")?.Length == 1 && issuer.Length <= 2048);
            var message = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>MCU StudioX</title>授权响应已收到，请返回 MCU StudioX。");
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = valid || validDenial ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            context.Response.Close();
            if (validDenial)
            {
                throw new EspressifAuthorizationDeniedException();
            }
            if (!valid)
            {
                throw new EspressifAuthenticationRequiredException();
            }
            return new AuthorizationResult { Code = code!, State = state!, Iss = issuer };
        }
    }

    public void Dispose()
    {
        listener.Close();
    }
}

internal sealed class EspressifAuthenticationRequiredException : Exception
{
    public EspressifAuthenticationRequiredException() : base("请在设置中显式连接乐鑫文档服务。")
    {
    }
}

internal sealed class EspressifAuthorizationDeniedException : Exception
{
    public EspressifAuthorizationDeniedException() : base("用户拒绝了乐鑫文档授权。")
    {
    }
}

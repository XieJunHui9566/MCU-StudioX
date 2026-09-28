namespace StudioX.Application.Espressif;

using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;

/// <summary>保留官方正文和来源，限制进入模型上下文的大小并移除授权凭据。</summary>
internal static class EspressifDocumentationReply
{
    public const string VersionNote = "乐鑫官方 MCP 当前仅索引最新 ESP-IDF；IDE 当前集成 ESP-IDF 5.5.4。请核对工程锁定版本、来源 URL 版本及本机 SDK，不能据此替换锁定 SDK。";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Links = new("https://[^\\s<>\"'\\\\]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex AuthorizationLinks = new("https://mcp\\.espressif\\.com/docs/authorize[^\\s<>\"']*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex SensitiveFields = new("(?i)(access_token|refresh_token|client_secret|authorization_code|code_verifier)([\\s\"':=]+)[^\\s,;\"'}]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static string Result(CallToolResult result, string query, string language, DateTimeOffset retrievedAt,
        TokenContainer? tokens)
    {
        var texts = result.Content.OfType<TextContentBlock>().Select(x => x.Text).ToList();
        if (result.StructuredContent is { } structured)
        {
            texts.Add(structured.GetRawText());
        }
        var excerpts = new List<string>();
        var citations = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var remaining = 24000;
        foreach (var text in texts)
        {
            var safe = Sanitize(text, tokens);
            foreach (Match match in Links.Matches(safe))
            {
                var candidate = match.Value.TrimEnd('.', ',', ')', ']', '}');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && IsSource(uri))
                {
                    if (citations.Count < 64)
                    {
                        citations.Add(uri.AbsoluteUri);
                    }
                }
            }
            if (safe.Length > remaining)
            {
                truncated = true;
                safe = safe[..remaining];
            }
            if (safe.Length > 0)
            {
                excerpts.Add(safe);
                remaining -= safe.Length;
            }
            if (remaining == 0)
            {
                truncated |= texts.Count > excerpts.Count;
                break;
            }
        }
        return JsonSerializer.Serialize(new
        {
            status = result.IsError == true ? "remote_error" : "ok",
            endpoint = EspressifDocumentationService.Endpoint.AbsoluteUri,
            source = "Espressif official documentation MCP",
            versionNote = VersionNote,
            query,
            language,
            retrievedAtUtc = retrievedAt,
            fromCache = false,
            truncated,
            citations = citations.ToArray(),
            excerpts,
            untrustedContent = true
        }, Json);
    }

    public static string Failure(string status, string message, int? httpStatus = null, string? retryAfter = null,
        string? diagnosticType = null) => JsonSerializer.Serialize(new
        {
            status,
            endpoint = EspressifDocumentationService.Endpoint.AbsoluteUri,
            versionNote = VersionNote,
            message,
            retrievedAtUtc = (DateTimeOffset?)null,
            fromCache = false,
            truncated = false,
            citations = Array.Empty<string>(),
            excerpts = Array.Empty<string>(),
            httpStatus,
            retryAfter,
            diagnosticType
        }, Json);

    private static bool IsSource(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals("espressif.com", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".espressif.com", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/espressif/", StringComparison.Ordinal)) &&
        !uri.AbsolutePath.StartsWith("/docs/authorize", StringComparison.Ordinal) &&
        !uri.Query.Contains("token", StringComparison.OrdinalIgnoreCase) &&
        !uri.Query.Contains("code=", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value, TokenContainer? tokens)
    {
        foreach (var secret in new[] { tokens?.AccessToken, tokens?.RefreshToken, tokens?.ClientSecret })
        {
            if (!string.IsNullOrEmpty(secret))
            {
                value = value.Replace(secret, "[credential redacted]", StringComparison.Ordinal);
            }
        }
        value = AuthorizationLinks.Replace(value, "[authorization URI redacted]");
        return SensitiveFields.Replace(value, "$1$2[credential redacted]");
    }
}

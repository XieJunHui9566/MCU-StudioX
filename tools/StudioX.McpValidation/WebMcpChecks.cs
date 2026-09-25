using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class WebMcpChecks
{
    private const string OfflineApiKey = "studiox-offline-test-key-never-expose";
    private const string ProviderSecret = "provider-error-secret-never-expose";
    private const string UrlSecret = "source-url-secret-never-expose";

    public static async Task RunAsync(StudioXMcpSession session, WorkbenchService services,
        string project, Action<bool, string> check)
    {
        var names = (await session.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        check(names.Contains("web_search") && names.Contains("web_fetch"),
            "built-in and external MCP catalogs advertise web search and page extraction");

        var requests = new List<(Uri Uri, string Body, string? Authorization)>();
        var documentText = "PUBLIC_REFERENCE_" + new string('x', 11_000);
        using var http = new HttpClient(new WebStubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            requests.Add((request.RequestUri!, body, request.Headers.Authorization?.ToString()));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/search")
            {
                using var payload = JsonDocument.Parse(body);
                var requested = payload.RootElement.GetProperty("max_results").GetInt32();
                var results = Enumerable.Range(0, requested).Select(index => new
                {
                    title = "Reference " + index,
                    url = "https://www.st.com/reference/" + index +
                        (index == 1 ? "?token=" + UrlSecret : ""),
                    content = "STM32F407 timer reference result " + index,
                    published_date = "2026-01-01"
                });
                return Json(HttpStatusCode.OK, new { query = "STM32F407 timer", results });
            }
            if (path == "/extract")
                return Json(HttpStatusCode.OK, new
                {
                    results = new[] { new { url = "https://www.st.com/reference/0", raw_content = documentText } },
                    failed_results = Array.Empty<object>()
                });
            throw new Exception("Unexpected Tavily endpoint in offline test: " + path);
        }));
        var research = new WebResearchService(http, () => OfflineApiKey);
        await using var webSession = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer(),
                webResearch: research));

        var first = await webSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
        {
            query = "STM32F407 timer", maxResults = 3, offset = 0
        }));
        if (!first.Contains("\"status\":\"ok\"", StringComparison.Ordinal))
            throw new Exception("Offline web_search fixture failed: " + first);
        using (var response = JsonDocument.Parse(first))
        {
            var value = response.RootElement;
            check(value.GetProperty("status").GetString() == "ok" &&
                  value.GetProperty("results").GetArrayLength() == 3 &&
                  value.GetProperty("results")[0].GetProperty("url").GetString() ==
                      "https://www.st.com/reference/0" &&
                  value.GetProperty("results")[1].GetProperty("url").GetString() ==
                      "https://www.st.com/reference/1" &&
                  value.GetProperty("results")[1].GetProperty("urlQueryRedacted").GetBoolean() &&
                  value.GetProperty("nextOffset").GetInt32() == 3 &&
                  value.GetProperty("untrustedContent").GetBoolean() &&
                  !first.Contains(UrlSecret, StringComparison.Ordinal),
                "web_search returns a bounded, source-attributed first page with continuation");
        }
        var second = await webSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
        {
            query = "STM32F407 timer", maxResults = 3, offset = 3
        }));
        using (var response = JsonDocument.Parse(second))
        {
            var value = response.RootElement;
            check(value.GetProperty("status").GetString() == "ok" &&
                  value.GetProperty("results").GetArrayLength() == 3 &&
                  value.GetProperty("results")[0].GetProperty("url").GetString() ==
                      "https://www.st.com/reference/3" &&
                  value.GetProperty("nextOffset").GetInt32() == 6,
                "web_search offset retrieves the next bounded result page");
        }

        var fetch = await webSession.CallToolAsync("web_fetch", JsonSerializer.Serialize(new
        {
            url = "https://www.st.com/reference/0", offsetCharacters = 0, maxCharacters = 1000
        }));
        var requestsBeforeFetchContinuation = requests.Count;
        using (var response = JsonDocument.Parse(fetch))
        {
            var value = response.RootElement;
            check(value.GetProperty("status").GetString() == "ok" &&
                  value.GetProperty("content").GetString()!.Length == 1000 &&
                  value.GetProperty("nextOffsetCharacters").GetInt32() == 1000 &&
                  value.GetProperty("totalCharacters").GetInt32() == documentText.Length &&
                  value.GetProperty("untrustedContent").GetBoolean(),
                "web_fetch limits extracted text and reports character continuation");
        }
        var fetchNext = await webSession.CallToolAsync("web_fetch", JsonSerializer.Serialize(new
        {
            url = "https://www.st.com/reference/0", offsetCharacters = 1000, maxCharacters = 1000
        }));
        using (var response = JsonDocument.Parse(fetchNext))
            check(response.RootElement.GetProperty("content").GetString() ==
                  documentText.Substring(1000, 1000),
                "web_fetch continuation does not reread or return the first text segment");

        check(requests.Count == requestsBeforeFetchContinuation &&
              requests.Count is >= 2 and <= 3 &&
              requests.Count(item => item.Uri.AbsolutePath == "/extract") == 1 &&
              requests.All(item =>
                  item.Uri.Scheme == Uri.UriSchemeHttps &&
                  item.Uri.Host == "api.tavily.com" &&
                  item.Uri.Query.Length == 0 &&
                  item.Authorization == "Bearer " + OfflineApiKey) &&
              !string.Join("\n", first, second, fetch, fetchNext).Contains(OfflineApiKey,
                  StringComparison.Ordinal),
            "Tavily key is sent only to the fixed API host and never returned to the model; fetch continuation reuses extraction");

        var requestCount = requests.Count;
        var badUrls = new[]
        {
            "file:///C:/Windows/win.ini",
            "http://www.st.com/reference/0",
            "https://user:password@www.st.com/reference/0",
            "https://127.0.0.1/admin",
            "https://192.168.1.1/admin",
            "https://[::1]/admin",
            "https://[::ffff:127.0.0.1]/admin",
            "https://169.254.169.254/latest/meta-data/",
            "https://localhost/admin",
            "https://router.local/admin",
            "https://service.internal/admin",
            "https://metadata.google.internal/admin",
            "https://www.st.com\\@127.0.0.1/reference/0",
            "https://www.st.com/reference/0?token=" + UrlSecret,
            "https://www.st.com:444/reference/0",
            "https://www.st.com/reference/0#fragment"
        };
        var rejected = true;
        foreach (var url in badUrls)
        {
            var result = await webSession.CallToolAsync("web_fetch", JsonSerializer.Serialize(new { url }));
            rejected &= result.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                !result.Contains(UrlSecret, StringComparison.Ordinal);
        }
        check(rejected && requests.Count == requestCount,
            "web_fetch rejects local, private, credential-bearing and non-HTTPS URLs before network I/O");

        var invalidSearch = await webSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
        {
            query = "STM32F407 timer", maxResults = 11, offset = 0
        }));
        var invalidFetchPage = await webSession.CallToolAsync("web_fetch", JsonSerializer.Serialize(new
        {
            url = "https://www.st.com/reference/0", offsetCharacters = 0, maxCharacters = 12001
        }));
        check(invalidSearch.Contains("error", StringComparison.OrdinalIgnoreCase) &&
              invalidFetchPage.Contains("error", StringComparison.OrdinalIgnoreCase) &&
              requests.Count == requestCount,
            "web_search result caps and web_fetch text caps are checked before provider I/O");

        string? keylessMode = null;
        string? keylessSource = null;
        string? keylessAuthorization = null;
        using var keylessHttp = new HttpClient(new WebStubHandler(request =>
        {
            keylessAuthorization = request.Headers.Authorization?.ToString();
            keylessMode = request.Headers.TryGetValues("X-Tavily-Access-Mode", out var modes)
                ? modes.SingleOrDefault() : null;
            keylessSource = request.Headers.TryGetValues("X-Client-Source", out var sources)
                ? sources.SingleOrDefault() : null;
            return Task.FromResult(Json(HttpStatusCode.OK, new { results = Array.Empty<object>() }));
        }));
        await using (var keylessSession = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer(),
                webResearch: new WebResearchService(keylessHttp, () => null))))
        {
            var result = await keylessSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
            {
                query = "STM32F407 timer", maxResults = 3
            }));
            check(result.Contains("\"status\":\"ok\"", StringComparison.Ordinal) &&
                  keylessAuthorization is null && keylessMode == "keyless" &&
                  keylessSource == "tavily-mcp-keyless",
                "web_search can use Tavily keyless mode without exposing or inventing a credential");
        }

        using var oversizedHttp = new HttpClient(new WebStubHandler(_ => Task.FromResult(
            Json(HttpStatusCode.OK, new { results = Array.Empty<object>(),
                padding = new string('x', 600_000) }))));
        await using (var oversizedSession = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer(),
                webResearch: new WebResearchService(oversizedHttp, () => OfflineApiKey))))
        {
            var result = await oversizedSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
            {
                query = "STM32F407 timer", maxResults = 3
            }));
            check(result.Contains("MCP_WEB_RESPONSE", StringComparison.Ordinal) &&
                  !result.Contains(OfflineApiKey, StringComparison.Ordinal),
                "oversized Tavily responses are rejected without returning provider data or the key");
        }

        using var failureHttp = new HttpClient(new WebStubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"" + ProviderSecret + "\"}",
                    Encoding.UTF8, "application/json")
            })));
        var failedResearch = new WebResearchService(failureHttp, () => OfflineApiKey);
        await using var failedSession = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer(),
                webResearch: failedResearch));
        var limited = await failedSession.CallToolAsync("web_search", JsonSerializer.Serialize(new
        {
            query = "STM32F407 timer", maxResults = 3
        }));
        check((limited.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
               limited.Contains("429", StringComparison.Ordinal)) &&
              !limited.Contains(ProviderSecret, StringComparison.Ordinal) &&
              !limited.Contains(OfflineApiKey, StringComparison.Ordinal),
            "provider rate limits are actionable without leaking API key or raw provider errors");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class WebStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}

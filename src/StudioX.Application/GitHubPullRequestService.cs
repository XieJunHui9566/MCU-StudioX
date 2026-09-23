namespace StudioX.Application;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;

/// <summary>GitHub PR 的独立 API 边界；凭据每次从 GCM 获取，只在请求头中使用。</summary>
public sealed class GitHubPullRequestService : IDisposable
{
    private const string ApiVersion = "2026-03-10";
    private readonly Func<string?, CancellationToken, Task<string>> tokenProvider;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public GitHubPullRequestService(GitHubAuthenticationService authentication, HttpClient? client = null)
        : this((account, token) => authentication.GetTokenAsync(account, token), client) { }

    /// <summary>允许离线测试注入假 HTTP 处理器和假凭据提供器。</summary>
    public GitHubPullRequestService(Func<string?, CancellationToken, Task<string>> tokenProvider, HttpClient? client = null)
    {
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        ownsClient = client is null;
        if (ownsClient) this.client.Timeout = TimeSpan.FromSeconds(45);
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }

    /// <summary>只接受 github.com 的 HTTPS 或 SSH Git 远端；拒绝 URL 内嵌凭据和任意 API 主机。</summary>
    public static GitHubRepository? TryParseRepository(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return null;
        var value = remoteUrl.Trim();
        string? path = null;
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = value["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && ((uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo))
                || (uri.Scheme == Uri.UriSchemeSsh && (uri.IsDefaultPort || uri.Port == 22)
                    && uri.UserInfo == "git")))
        {
            path = uri.AbsolutePath.TrimStart('/');
            if (path.Contains('%')) return null;
        }
        if (path is null || path.Contains('\\')) return null;
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/');
        if (parts.Length != 2 || !ValidOwner(parts[0]) || !ValidRepositoryName(parts[1])) return null;
        return new GitHubRepository(parts[0], parts[1]);
    }

    public async Task<IReadOnlyList<GitHubPullRequest>> ListAsync(GitHubRepository repository,
        GitHubPullRequestState state = GitHubPullRequestState.Open, CancellationToken token = default,
        string? account = null)
    {
        var path = RepositoryPath(repository) + "/pulls";
        var result = new List<GitHubPullRequest>();
        for (var page = 1; page <= 10; page++)
        {
            using var document = await GetJsonAsync($"{path}?state={state.ToString().ToLowerInvariant()}&sort=updated&direction=desc&per_page=100&page={page}", account, token);
            var entries = document.RootElement.EnumerateArray().ToArray();
            result.AddRange(entries.Select(ParsePullRequest));
            if (entries.Length < 100) return result;
        }
        throw new InvalidDataException("GitHub PR 列表超过 1000 条；请缩小查询范围。");
    }

    public async Task<GitHubPullRequestDetails> GetDetailsAsync(GitHubRepository repository, int number,
        CancellationToken token = default, string? account = null)
    {
        var path = PullPath(repository, number);
        using var pr = await GetJsonAsync(path, account, token);
        var pull = ParsePullRequest(pr.RootElement);
        var files = await GetPagesAsync(path + "/files", ParseFile, account, token);
        var reviews = await GetPagesAsync(path + "/reviews", ParseReview, account, token);
        var comments = await GetPagesAsync(RepositoryPath(repository) + $"/issues/{number}/comments", ParseComment, account, token);
        var reviewComments = await GetPagesAsync(path + "/comments", ParseReviewComment, account, token);
        var checks = await GetChecksAsync(repository, pull.HeadSha, token, account);
        return new(pull, files, reviews, comments, reviewComments, checks);
    }

    /// <summary>读取 GitHub 当前默认分支，避免新建 PR 时猜测 main 或 master。</summary>
    public async Task<string> GetDefaultBranchAsync(GitHubRepository repository,
        CancellationToken token = default, string? account = null)
    {
        using var result = await GetJsonAsync(RepositoryPath(repository), account, token);
        var branch = OptionalString(result.RootElement, "default_branch");
        if (string.IsNullOrWhiteSpace(branch))
            throw new InvalidDataException("GitHub 没有返回仓库默认分支；请手动指定目标分支。");
        return branch;
    }

    public async Task<GitHubPullRequest> CreateAsync(GitHubRepository repository,
        GitHubCreatePullRequest request, CancellationToken token = default, string? account = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256
            || string.IsNullOrWhiteSpace(request.Head) || string.IsNullOrWhiteSpace(request.Base))
            throw new ArgumentException("PR 标题、源分支和目标分支不能为空。", nameof(request));
        using var result = await SendJsonAsync(HttpMethod.Post, RepositoryPath(repository) + "/pulls",
            new { title = request.Title.Trim(), body = request.Body ?? "", head = request.Head.Trim(),
                @base = request.Base.Trim(), draft = request.Draft }, account, token);
        return ParsePullRequest(result.RootElement);
    }

    /// <summary>普通会话评论写入 PR 时间线；逐行评论属于另一套 review comments API。</summary>
    public async Task<GitHubPullRequestComment> CommentAsync(GitHubRepository repository, int number,
        string body, CancellationToken token = default, string? account = null)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("评论不能为空。", nameof(body));
        using var result = await SendJsonAsync(HttpMethod.Post,
            RepositoryPath(repository) + $"/issues/{RequireNumber(number)}/comments", new { body = body.Trim() }, account, token);
        return ParseComment(result.RootElement);
    }

    public async Task<GitHubPullRequestReview> ReviewAsync(GitHubRepository repository, int number,
        GitHubReviewEvent reviewEvent, string body, CancellationToken token = default, string? account = null)
    {
        if (!Enum.IsDefined(reviewEvent)) throw new ArgumentOutOfRangeException(nameof(reviewEvent));
        if (reviewEvent != GitHubReviewEvent.Approve && string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("评论或请求修改时必须填写审阅意见。", nameof(body));
        var action = reviewEvent switch
        {
            GitHubReviewEvent.Approve => "APPROVE",
            GitHubReviewEvent.RequestChanges => "REQUEST_CHANGES",
            _ => "COMMENT"
        };
        using var result = await SendJsonAsync(HttpMethod.Post, PullPath(repository, number) + "/reviews",
            new { body = body?.Trim() ?? "", @event = action }, account, token);
        return ParseReview(result.RootElement);
    }

    /// <summary>expectedHeadSha 交由 GitHub 原子校验；服务端保护规则与检查状态仍是最终裁决。</summary>
    public async Task<GitHubMergeResult> MergeAsync(GitHubRepository repository, int number,
        string expectedHeadSha, GitHubMergeMethod method, CancellationToken token = default,
        string? account = null)
    {
        if (!Regex.IsMatch(expectedHeadSha ?? "", "\\A[0-9a-fA-F]{40,64}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("合并前需要当前 PR 源提交的 SHA。", nameof(expectedHeadSha));
        if (!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
        using var result = await SendJsonAsync(HttpMethod.Put, PullPath(repository, number) + "/merge",
            new { sha = expectedHeadSha, merge_method = method.ToString().ToLowerInvariant() }, account, token);
        return new GitHubMergeResult(GetBoolean(result.RootElement, "merged") ?? false,
            GetString(result.RootElement, "sha"), GetString(result.RootElement, "message"));
    }

    public async Task<GitHubPullRequestChecks> GetChecksAsync(GitHubRepository repository,
        string headSha, CancellationToken token = default, string? account = null)
    {
        if (!Regex.IsMatch(headSha ?? "", "\\A[0-9a-fA-F]{40,64}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("检查状态需要有效的源提交 SHA。", nameof(headSha));
        var path = RepositoryPath(repository) + "/commits/" + headSha;
        string status;
        try
        {
            using var combined = await GetJsonAsync(path + "/status", account, token);
            status = GetString(combined.RootElement, "state");
            using var runs = await GetJsonAsync(path + "/check-runs?per_page=100", account, token);
            var items = Property(runs.RootElement, "check_runs").EnumerateArray()
                .Select(e => new GitHubCheckRun(GetString(e, "name"), GetString(e, "status"),
                    OptionalString(e, "conclusion"), OptionalUri(e, "html_url"))).ToArray();
            var total = GetInt(runs.RootElement, "total_count");
            return new GitHubPullRequestChecks(status, items,
                total > items.Length ? $"共有 {total} 项检查，仅显示前 {items.Length} 项。" : null);
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // 检查数据的授权可能与 PR 不同。显式呈现不可用，合并仍由 GitHub 保护规则决定。
            return new GitHubPullRequestChecks("unknown", [], ex.Message);
        }
    }

    private async Task<IReadOnlyList<T>> GetPagesAsync<T>(string path, Func<JsonElement, T> parse,
        string? account, CancellationToken token)
    {
        var result = new List<T>();
        for (var page = 1; page <= 10; page++)
        {
            using var document = await GetJsonAsync($"{path}?per_page=100&page={page}", account, token);
            var entries = document.RootElement.EnumerateArray().ToArray();
            result.AddRange(entries.Select(parse));
            if (entries.Length < 100) return result;
        }
        throw new InvalidDataException("GitHub 返回超过 1000 条记录，当前列表无法完整显示。");
    }

    private Task<JsonDocument> GetJsonAsync(string path, string? account, CancellationToken token) =>
        SendJsonAsync(HttpMethod.Get, path, null, account, token);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body,
        string? account, CancellationToken token)
    {
        // path 仅由本服务生成；认证值不能进入 URL、异常文本或日志。
        var credential = await tokenProvider(account, token);
        if (string.IsNullOrWhiteSpace(credential) || credential.Contains('\r') || credential.Contains('\n'))
            throw new InvalidOperationException("GitHub 凭据不可用，请先登录账号。");
        using var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("MCU-StudioX/0.2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw await MakeApiErrorAsync(response, credential, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static async Task<GitHubApiException> MakeApiErrorAsync(HttpResponseMessage response,
        string credential, CancellationToken token)
    {
        string? serverMessage = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            serverMessage = OptionalString(document.RootElement, "message");
        }
        catch (JsonException) { }
        var prefix = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "登录已过期或 GitHub 拒绝凭据，请重新登录。",
            HttpStatusCode.Forbidden => "GitHub 拒绝操作：可能缺少仓库权限、受到组织策略限制或达到速率限制。",
            HttpStatusCode.NotFound => "仓库或 PR 不存在，或当前账号没有访问权限。",
            HttpStatusCode.MethodNotAllowed => "GitHub 不允许合并：请检查冲突、分支保护、审阅和状态检查。",
            HttpStatusCode.Conflict => "远端已变化或合并冲突，请刷新 PR 后重试。",
            HttpStatusCode.UnprocessableEntity => "GitHub 拒绝请求：请检查分支、权限、合并条件及输入内容。",
            _ => $"GitHub API 请求失败（HTTP {(int)response.StatusCode}）。"
        };
        var safe = serverMessage?.Replace(credential, "[redacted]", StringComparison.Ordinal) ?? "";
        if (safe.Length > 400) safe = safe[..400];
        return new GitHubApiException(response.StatusCode, string.IsNullOrWhiteSpace(safe) ? prefix : prefix + " " + safe);
    }

    private static string RepositoryPath(GitHubRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (!ValidOwner(repository.Owner) || !ValidRepositoryName(repository.Name))
            throw new ArgumentException("GitHub 仓库名称无效。", nameof(repository));
        return $"repos/{repository.Owner}/{repository.Name}";
    }

    private static string PullPath(GitHubRepository repository, int number) =>
        RepositoryPath(repository) + "/pulls/" + RequireNumber(number);

    private static int RequireNumber(int number) => number > 0 ? number : throw new ArgumentOutOfRangeException(nameof(number));
    private static bool ValidOwner(string value) => Regex.IsMatch(value ?? "", "\\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\\z", RegexOptions.CultureInvariant);
    private static bool ValidRepositoryName(string value) => Regex.IsMatch(value ?? "", "\\A[A-Za-z0-9._-]{1,100}\\z", RegexOptions.CultureInvariant)
        && value is not "." and not "..";

    private static GitHubPullRequest ParsePullRequest(JsonElement e)
    {
        var head = Property(e, "head"); var target = Property(e, "base");
        return new GitHubPullRequest(GetInt(e, "number"), GetString(e, "title"), GetString(e, "body"),
            GetString(e, "state"), GetBoolean(e, "draft") ?? false,
            GetBoolean(e, "merged") ?? OptionalString(e, "merged_at") is not null,
            GetString(Property(e, "user"), "login"), GetString(head, "ref"), GetString(head, "sha"),
            GetString(target, "ref"), GetString(Property(head, "repo"), "full_name"),
            GetString(Property(target, "repo"), "full_name"),
            OptionalUri(e, "html_url") ?? new Uri("https://github.com/"),
            GetDate(e, "updated_at"), GetBoolean(e, "mergeable"), OptionalString(e, "mergeable_state"));
    }

    private static GitHubPullRequestFile ParseFile(JsonElement e) => new(GetString(e, "filename"),
        GetString(e, "status"), GetInt(e, "additions"), GetInt(e, "deletions"), OptionalString(e, "patch"));

    private static GitHubPullRequestReview ParseReview(JsonElement e) => new(
        GetString(Property(e, "user"), "login"), GetString(e, "state"), GetString(e, "body"),
        GetDateOrNull(e, "submitted_at"));

    private static GitHubPullRequestComment ParseComment(JsonElement e) => new(
        GetString(Property(e, "user"), "login"), GetString(e, "body"), GetDate(e, "created_at"),
        OptionalUri(e, "html_url"));

    private static GitHubPullRequestReviewComment ParseReviewComment(JsonElement e) => new(
        GetLong(e, "id"), GetString(Property(e, "user"), "login"), GetString(e, "path"),
        GetIntOrNull(e, "line"), OptionalString(e, "side"), GetIntOrNull(e, "original_line"),
        GetString(e, "body"), GetDate(e, "created_at"), OptionalUri(e, "html_url"),
        GetLongOrNull(e, "in_reply_to_id"));

    private static JsonElement Property(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;
    private static string GetString(JsonElement e, string name) => OptionalString(e, name) ?? "";
    private static string? OptionalString(JsonElement e, string name)
    {
        var value = Property(e, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int GetInt(JsonElement e, string name)
    {
        var value = Property(e, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    }
    private static int? GetIntOrNull(JsonElement e, string name)
    {
        var value = Property(e, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }
    private static long GetLong(JsonElement e, string name) => GetLongOrNull(e, name) ?? 0;
    private static long? GetLongOrNull(JsonElement e, string name)
    {
        var value = Property(e, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
    private static bool? GetBoolean(JsonElement e, string name)
    {
        var value = Property(e, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    }
    private static Uri? OptionalUri(JsonElement e, string name)
    {
        var text = OptionalString(e, name);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }
    private static DateTimeOffset GetDate(JsonElement e, string name) => GetDateOrNull(e, name) ?? DateTimeOffset.MinValue;
    private static DateTimeOffset? GetDateOrNull(JsonElement e, string name) =>
        DateTimeOffset.TryParse(OptionalString(e, name), out var date) ? date : null;
}

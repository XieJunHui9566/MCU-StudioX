using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var expectedSha = new string('a', 40);
var fakeToken = "offline-test-secret";
var seen = new List<(string Method, string Path, string? Body)>();
var requestedAccounts = new List<string?>();
var pullJson = $$"""
{
  "number": 7, "title": "Review firmware", "body": "Check timer", "state": "open",
  "draft": false, "merged": false, "mergeable": true, "mergeable_state": "clean",
  "user": { "login": "alice" }, "head": { "ref": "feature/timer", "sha": "{{expectedSha}}", "repo": { "full_name": "acme/firmware" } },
  "base": { "ref": "main", "repo": { "full_name": "acme/firmware" } },
  "html_url": "https://github.com/acme/firmware/pull/7", "updated_at": "2026-09-24T04:00:00Z"
}
""";

var handler = new StubHandler(async request =>
{
    Check(request.RequestUri?.Host == "api.github.com", "API host is fixed");
    Check(request.Headers.Authorization?.Scheme == "Bearer" &&
          request.Headers.Authorization.Parameter == fakeToken, "token is in Authorization header");
    Check(request.Headers.Contains("X-GitHub-Api-Version"), "GitHub API version header");
    var path = request.RequestUri!.PathAndQuery;
    var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
    seen.Add((request.Method.Method, path, body));
    if (request.Method == HttpMethod.Put && path.EndsWith("/merge", StringComparison.Ordinal))
    {
        Check(body?.Contains(expectedSha, StringComparison.Ordinal) == true, "conditional merge SHA");
        if (body?.Contains("\"merge_method\":\"squash\"", StringComparison.Ordinal) != true)
            throw new Exception("merge method missing");
        return Json(HttpStatusCode.OK, """{"merged":true,"sha":"merged-sha","message":"Pull Request successfully merged"}""");
    }
    if (request.Method == HttpMethod.Post && path.EndsWith("/pulls", StringComparison.Ordinal))
    {
        Check(body?.Contains("\"head\":\"feature/timer\"", StringComparison.Ordinal) == true, "create head branch");
        return Json(HttpStatusCode.Created, pullJson);
    }
    if (request.Method == HttpMethod.Post && path.EndsWith("/comments", StringComparison.Ordinal))
        return Json(HttpStatusCode.Created,
            """{"body":"Looks good","user":{"login":"bob"},"created_at":"2026-09-24T04:00:00Z","html_url":"https://github.com/acme/firmware/pull/7#issuecomment-1"}""");
    if (request.Method == HttpMethod.Post && path.EndsWith("/reviews", StringComparison.Ordinal))
    {
        Check(body?.Contains("\"event\":\"APPROVE\"", StringComparison.Ordinal) == true, "review event");
        return Json(HttpStatusCode.OK,
            """{"state":"APPROVED","body":"Approved","user":{"login":"bob"},"submitted_at":"2026-09-24T04:00:00Z"}""");
    }
    if (path.StartsWith("/repos/acme/firmware/pulls?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, "[" + pullJson + "]");
    if (path == "/repos/acme/firmware/pulls/7") return Json(HttpStatusCode.OK, pullJson);
    if (path.StartsWith("/repos/acme/firmware/pulls/7/files?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK,
            """[{"filename":"src/main.c","status":"modified","additions":2,"deletions":1,"patch":"@@ -1 +1 @@"}]""");
    if (path.StartsWith("/repos/acme/firmware/pulls/7/reviews?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK,
            """[{"state":"APPROVED","body":"Okay","user":{"login":"bob"},"submitted_at":"2026-09-24T04:00:00Z"}]""");
    if (path.StartsWith("/repos/acme/firmware/pulls/7/comments?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK,
            """[{"id":99,"body":"Handle rollover","path":"src/main.c","line":42,"side":"RIGHT","original_line":42,"user":{"login":"reviewer"},"created_at":"2026-09-24T04:01:00Z","html_url":"https://github.com/acme/firmware/pull/7#discussion_r99"},{"id":100,"body":"Outdated note","path":"src/main.c","line":null,"side":"LEFT","original_line":12,"in_reply_to_id":99,"user":{"login":"alice"},"created_at":"2026-09-24T04:02:00Z"}]""");
    if (path.StartsWith("/repos/acme/firmware/issues/7/comments?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK,
            """[{"body":"Ready","user":{"login":"carol"},"created_at":"2026-09-24T04:00:00Z"}]""");
    if (path == "/repos/acme/firmware")
        return Json(HttpStatusCode.OK, """{"full_name":"acme/firmware","default_branch":"release/2026"}""");
    if (path.EndsWith("/status", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, """{"state":"success"}""");
    if (path.Contains("/check-runs?", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK,
            """{"check_runs":[{"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/acme/firmware/actions/runs/1"}]}""");
    throw new Exception("Unexpected API route: " + path);
});

using var client = new HttpClient(handler);
using var service = new GitHubPullRequestService((account, _) =>
{
    requestedAccounts.Add(account);
    return Task.FromResult(fakeToken);
}, client);

var repository = new GitHubRepository("acme", "firmware");
Check(GitHubPullRequestService.TryParseRepository("https://github.com/acme/firmware.git") == repository,
    "HTTPS remote parse");
Check(GitHubPullRequestService.TryParseRepository("git@github.com:acme/firmware.git") == repository,
    "SSH remote parse");
Check(GitHubPullRequestService.TryParseRepository("ssh://git@github.com/acme/firmware.git") == repository,
    "SSH URL remote parse");
foreach (var invalid in new[] { "http://github.com/acme/firmware.git",
    "https://github.com.evil.test/acme/firmware.git", "https://user:secret@github.com/acme/firmware.git",
    "https://github.com/acme/firmware.git/other", "file:///repo.git", "https://github.com/acme/../firmware.git" })
    Check(GitHubPullRequestService.TryParseRepository(invalid) is null, "reject remote " + invalid);

var list = await service.ListAsync(repository, account: "alice");
Check(list.Count == 1 && list[0].Number == 7 && list[0].HeadSha == expectedSha, "list and parse PR");
var details = await service.GetDetailsAsync(repository, 7, account: "alice");
Check(details.Files.Count == 1 && details.Reviews.Count == 1 && details.Comments.Count == 1
      && details.Checks.CommitStatus == "success" && details.Checks.Runs.Count == 1,
    "details, reviews, comments and checks");
Check(details.ReviewComments.Count == 2 && details.ReviewComments[0].Author == "reviewer"
      && details.ReviewComments[0].Path == "src/main.c" && details.ReviewComments[0].Line == 42
      && details.ReviewComments[0].Side == "RIGHT" && details.ReviewComments[0].Body == "Handle rollover"
      && details.ReviewComments[0].CreatedAt.Year == 2026
      && details.ReviewComments[1].Line is null && details.ReviewComments[1].OriginalLine == 12
      && details.ReviewComments[1].ReplyToId == 99,
    "current and outdated line review comments");
Check(await service.GetDefaultBranchAsync(repository, account: "alice") == "release/2026",
    "default branch comes from GitHub rather than a main/master guess");
var created = await service.CreateAsync(repository,
    new GitHubCreatePullRequest("Review firmware", "Check timer", "feature/timer", "main"), account: "alice");
Check(created.Number == 7, "create PR");
var comment = await service.CommentAsync(repository, 7, "Looks good", account: "alice");
Check(comment.Author == "bob", "timeline comment");
var review = await service.ReviewAsync(repository, 7, GitHubReviewEvent.Approve, "Approved", account: "alice");
Check(review.State == "APPROVED", "review PR");
var merged = await service.MergeAsync(repository, 7, expectedSha, GitHubMergeMethod.Squash, account: "alice");
Check(merged.Merged, "merge PR");
Check(requestedAccounts.Count == seen.Count && requestedAccounts.All(a => a == "alice"),
    "selected account used for every API request");

using var deniedClient = new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.MethodNotAllowed,
    """{"message":"Required status check failed: offline-test-secret"}"""))));
using var denied = new GitHubPullRequestService((_, _) => Task.FromResult(fakeToken), deniedClient);
try
{
    await denied.MergeAsync(repository, 7, expectedSha, GitHubMergeMethod.Merge);
    throw new Exception("merge rejection was ignored");
}
catch (GitHubApiException error)
{
    Check(error.StatusCode == HttpStatusCode.MethodNotAllowed && error.Message.Contains("分支保护", StringComparison.Ordinal)
          && !error.Message.Contains(fakeToken, StringComparison.Ordinal), "protected branch error and token redaction");
}

using var staleClient = new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.Conflict,
    """{"message":"Head branch was modified"}"""))));
using var stale = new GitHubPullRequestService((_, _) => Task.FromResult(fakeToken), staleClient);
try
{
    await stale.MergeAsync(repository, 7, expectedSha, GitHubMergeMethod.Merge);
    throw new Exception("stale head was ignored");
}
catch (GitHubApiException error)
{
    Check(error.StatusCode == HttpStatusCode.Conflict && error.Message.Contains("刷新", StringComparison.Ordinal),
        "stale head guidance");
}

using var noChecksClient = new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.Forbidden,
    """{"message":"Resource not accessible by integration"}"""))));
using var noChecks = new GitHubPullRequestService((_, _) => Task.FromResult(fakeToken), noChecksClient);
var unavailable = await noChecks.GetChecksAsync(repository, expectedSha);
Check(unavailable.CommitStatus == "unknown" && unavailable.UnavailableReason?.Contains("权限", StringComparison.Ordinal) == true,
    "checks permission issue remains visible");

using var noDefaultClient = new HttpClient(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}"))));
using var noDefault = new GitHubPullRequestService((_, _) => Task.FromResult(fakeToken), noDefaultClient);
try
{
    await noDefault.GetDefaultBranchAsync(repository);
    throw new Exception("missing default branch was silently guessed");
}
catch (InvalidDataException error)
{
    Check(error.Message.Contains("默认分支", StringComparison.Ordinal), "missing default branch guidance");
}

Console.WriteLine($"PASS: {seen.Count} offline GitHub API requests, account routing, conditional merge, safety errors");

static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => respond(request);
}

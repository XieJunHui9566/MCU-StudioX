using System.Reflection;
using System.Net;
using System.Text;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

var source = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var git = new GitRepositoryService(Path.Combine(source, "artifacts", "git-runtime"));
git.RequireCredentialManagerAvailable();
var runner = new ProcessRunner();
var checks = 0;

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

var help = await runner.RunAsync(new(git.CredentialManagerExecutable, ["github", "--help"], git.RootDirectory,
    TimeSpan.FromSeconds(20), git.Environment(), RemoveEnvironment: GitRepositoryService.AmbientVariables));
Check(help.Success && help.StandardOutput.Contains("login", StringComparison.Ordinal)
    && help.StandardOutput.Contains("list", StringComparison.Ordinal)
    && help.StandardOutput.Contains("logout", StringComparison.Ordinal), "bundled GCM exposes account commands");
var loginHelp = await runner.RunAsync(new(git.CredentialManagerExecutable, ["github", "login", "--help"], git.RootDirectory,
    TimeSpan.FromSeconds(20), git.Environment(), RemoveEnvironment: GitRepositoryService.AmbientVariables));
Check(loginHelp.Success && loginHelp.StandardOutput.Contains("--browser", StringComparison.Ordinal), "bundled GCM supports browser OAuth");

const string remoteUrl = "https://github.com/example/repo.git";
var credentialArguments = (IReadOnlyList<string>)typeof(GitRepositoryService)
    .GetMethod("GitHubCredentialConfigArguments", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(git, [remoteUrl])!;
var matchingHelper = await runner.RunAsync(new(git.Executable,
    ["-c", "credential.https://github.com.helper=outside-helper", .. credentialArguments,
        "config", "--get-urlmatch", "credential.helper", remoteUrl], git.RootDirectory,
    TimeSpan.FromSeconds(20), git.Environment(), RemoveEnvironment: GitRepositoryService.AmbientVariables));
Check(matchingHelper.Success && matchingHelper.StandardOutput.Trim() == "manager",
    "repository URL override defeats a broader external credential helper");

var type = typeof(GitHubAuthenticationService);
object Invoke(string name, params object[] values) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values)!;
var authentication = new GitHubAuthenticationService(git);
var environment = (Dictionary<string, string>)type.GetMethod("SafeEnvironment", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(authentication, [false])!;
Check(environment["GCM_CREDENTIAL_STORE"] == "wincredman" && environment["GCM_INTERACTIVE"] == "false"
    && environment["GCM_TRACE_SECRETS"] == "0", "token read requires Windows secure store without prompts or secret tracing");
var accounts = (IReadOnlyList<string>)Invoke("ParseAccounts", "alice\r\nbob\nalice\n");
Check(accounts.SequenceEqual(["alice", "bob"]), "account list strips empty lines and duplicates");

const string fakeSecret = "unit-test-credential";
Check((string)Invoke("ParseCredential", $"username=alice\npassword={fakeSecret}\n", "alice") == fakeSecret,
    "credential parser selects matching account");
try
{
    Invoke("ParseCredential", $"username=bob\npassword={fakeSecret}\n", "alice");
    throw new Exception("mismatched account was accepted");
}
catch (TargetInvocationException error) when (error.InnerException is StudioXException inner)
{
    Check(!inner.Message.Contains(fakeSecret, StringComparison.Ordinal), "credential error never includes token");
}
try
{
    Invoke("ValidateAccount", "alice\npassword=injected");
    throw new Exception("credential protocol injection was accepted");
}
catch (TargetInvocationException error) when (error.InnerException is StudioXException)
{
    Check(true, "account validation rejects protocol injection");
}

const string fakeProfileToken = "unit-test-profile-token";
var avatarBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
var avatarRequests = 0;
var selectedAccounts = new List<string?>();
using var profileClient = new HttpClient(new StubHandler(request =>
{
    if (request.RequestUri == new Uri("https://api.github.com/user"))
    {
        Check(request.Headers.Authorization?.Scheme == "Bearer"
            && request.Headers.Authorization.Parameter == fakeProfileToken,
            "profile API carries token only in Authorization header");
        Check(request.Headers.Contains("X-GitHub-Api-Version"), "profile API pins GitHub version");
        return Json(HttpStatusCode.OK,
            """{"login":"Alice","name":"Alice Example","avatar_url":"https://avatars.githubusercontent.com/u/123?v=4"}""");
    }
    if (request.RequestUri?.Host == "avatars.githubusercontent.com")
    {
        avatarRequests++;
        Check(request.Headers.Authorization is null, "avatar request has no OAuth header");
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(avatarBytes) };
        result.Content.Headers.ContentType = new("image/png");
        return result;
    }
    throw new Exception("Unexpected profile route: " + request.RequestUri);
}));
using var profileService = new GitHubProfileService((account, _) =>
{
    selectedAccounts.Add(account);
    return Task.FromResult(fakeProfileToken);
}, profileClient);
var profile = await profileService.GetProfileAsync("alice");
Check(profile.Login == "Alice" && profile.Name == "Alice Example"
    && profile.AvatarBytes?.SequenceEqual(avatarBytes) == true,
    "verified account profile includes GitHub login, name and avatar bytes");
Check(selectedAccounts.SequenceEqual(["alice"]) && avatarRequests == 1,
    "profile reads only selected GCM account and trusted avatar host");

using var mismatchClient = ProfileClient("""{"login":"bob","avatar_url":"https://avatars.githubusercontent.com/u/456"}""");
using var mismatchService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), mismatchClient);
try
{
    await mismatchService.GetProfileAsync("alice");
    throw new Exception("mismatched OAuth identity was accepted");
}
catch (InvalidDataException error)
{
    Check(!error.Message.Contains(fakeProfileToken, StringComparison.Ordinal),
        "profile rejects account mismatch without disclosing token");
}

using var rejectedAvatarClient = ProfileClient("""{"login":"alice","avatar_url":"https://avatars.githubusercontent.com.evil.test/u/1"}""");
using var rejectedAvatarService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), rejectedAvatarClient);
var rejectedAvatarProfile = await rejectedAvatarService.GetProfileAsync("alice");
Check(rejectedAvatarProfile.Login == "alice" && rejectedAvatarProfile.AvatarBytes is null,
    "untrusted avatar URL preserves login without issuing a request");

using var redirectClient = new HttpClient(new StubHandler(request => request.RequestUri?.Host switch
{
    "api.github.com" => Json(HttpStatusCode.OK,
        """{"login":"alice","avatar_url":"https://avatars.githubusercontent.com/u/1"}"""),
    "avatars.githubusercontent.com" => new HttpResponseMessage(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri("https://untrusted.example/avatar") }
    },
    _ => throw new Exception("Avatar redirect was followed")
}));
using var redirectService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), redirectClient);
var redirectProfile = await redirectService.GetProfileAsync("alice");
Check(redirectProfile.Login == "alice" && redirectProfile.AvatarBytes is null,
    "avatar redirect is rejected while retaining username");

using var largeAvatarClient = new HttpClient(new StubHandler(request =>
{
    if (request.RequestUri?.Host == "api.github.com") return Json(HttpStatusCode.OK,
        """{"login":"alice","avatar_url":"https://avatars.githubusercontent.com/u/1"}""");
    var oversized = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) };
    oversized.Content.Headers.ContentType = new("image/png");
    return oversized;
}));
using var largeAvatarService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), largeAvatarClient);
var largeAvatarProfile = await largeAvatarService.GetProfileAsync("alice");
Check(largeAvatarProfile.Login == "alice" && largeAvatarProfile.AvatarBytes is null,
    "oversized avatar is discarded without removing username");

using var corruptAvatarClient = new HttpClient(new StubHandler(request =>
{
    if (request.RequestUri?.Host == "api.github.com") return Json(HttpStatusCode.OK,
        """{"login":"alice","avatar_url":"https://avatars.githubusercontent.com/u/1"}""");
    var corrupt = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("not an image"u8.ToArray()) };
    corrupt.Content.Headers.ContentType = new("image/png");
    return corrupt;
}));
using var corruptAvatarService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), corruptAvatarClient);
var corruptAvatarProfile = await corruptAvatarService.GetProfileAsync("alice");
Check(corruptAvatarProfile.Login == "alice" && corruptAvatarProfile.AvatarBytes is null,
    "invalid image payload is discarded without removing username");

using var deniedClient = new HttpClient(new StubHandler(_ => Json(HttpStatusCode.Unauthorized,
    "{\"message\":\"" + fakeProfileToken + "\"}")));
using var deniedService = new GitHubProfileService((_, _) => Task.FromResult(fakeProfileToken), deniedClient);
try
{
    await deniedService.GetProfileAsync("alice");
    throw new Exception("unauthorized profile was accepted");
}
catch (GitHubApiException error)
{
    Check(error.StatusCode == HttpStatusCode.Unauthorized
        && !error.Message.Contains(fakeProfileToken, StringComparison.Ordinal),
        "profile API rejection does not surface response body or token");
}

Console.WriteLine($"PASS {checks} offline GitHub authentication checks; no account, network, or credential store access.");

static HttpClient ProfileClient(string body) => new(new StubHandler(request =>
{
    if (request.RequestUri?.Host != "api.github.com")
        throw new Exception("Unexpected avatar request: " + request.RequestUri);
    return Json(HttpStatusCode.OK, body);
}));

static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
{
    Content = new StringContent(value, Encoding.UTF8, "application/json")
};

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

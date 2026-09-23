namespace StudioX.Application;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;

/// <summary>已授权 GitHub.com 账号的显示资料。头像缺失不影响登录名展示。</summary>
public sealed record GitHubUserProfile(string Login, string? Name, byte[]? AvatarBytes);

/// <summary>从 GitHub 读取当前 OAuth 身份及头像，不将令牌交给桌面视图。</summary>
public sealed class GitHubProfileService : IDisposable
{
    private const string ApiVersion = "2026-03-10";
    private const int ProfileLimit = 128 * 1024;
    private const int AvatarLimit = 1024 * 1024;
    private static readonly Uri UserUri = new("https://api.github.com/user");
    private readonly Func<string?, CancellationToken, Task<string>> tokenProvider;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public GitHubProfileService(GitHubAuthenticationService authentication, HttpClient? client = null)
        : this((account, token) => authentication.GetTokenAsync(account, token), client) { }

    /// <summary>离线测试可注入假令牌及 HTTP；生产环境只使用内置 GCM。</summary>
    public GitHubProfileService(Func<string?, CancellationToken, Task<string>> tokenProvider, HttpClient? client = null)
    {
        this.tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        this.client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        ownsClient = client is null;
        if (ownsClient) this.client.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }

    public async Task<GitHubUserProfile> GetProfileAsync(string account, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("请先选择 GitHub 账号。", nameof(account));
        var credential = await tokenProvider(account, token);
        if (string.IsNullOrWhiteSpace(credential) || credential.Contains('\r') || credential.Contains('\n'))
            throw new InvalidOperationException("GitHub 凭据不可用，请重新登录。");

        using var request = new HttpRequestMessage(HttpMethod.Get, UserUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("MCU-StudioX/0.2");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException(response.StatusCode, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub 登录已过期，请重新登录。",
                HttpStatusCode.Forbidden => "GitHub 拒绝读取账号资料，请检查授权或网络限制。",
                _ => $"读取 GitHub 账号资料失败（HTTP {(int)response.StatusCode}）。"
            });

        var json = await ReadBoundedAsync(response.Content, ProfileLimit, token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var login = ReadOptionalString(root, "login");
        if (string.IsNullOrWhiteSpace(login)
            || !Regex.IsMatch(login, "\\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("GitHub 未返回有效的账号名称。");
        if (!string.Equals(login, account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 授权身份与选定账号不一致，请重新选择或登录账号。");

        var name = ReadOptionalString(root, "name");
        var avatar = await TryReadAvatarAsync(ReadOptionalString(root, "avatar_url"), token);
        return new GitHubUserProfile(login, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), avatar);
    }

    private async Task<byte[]?> TryReadAvatarAsync(string? url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var avatarUri) || !TrustedAvatarUri(avatarUri)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, avatarUri);
            request.Headers.Accept.ParseAdd("image/png, image/jpeg, image/gif");
            request.Headers.UserAgent.ParseAdd("MCU-StudioX/0.2");
            // 头像是公开资源；此请求绝不附带 OAuth token，也不跟随重定向。
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not ("image/png" or "image/jpeg" or "image/gif")) return null;
            var bytes = await ReadBoundedAsync(response.Content, AvatarLimit, token);
            return SupportedImage(bytes, mime) ? bytes : null;
        }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static bool TrustedAvatarUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Host.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/images/", StringComparison.Ordinal)));

    private static bool SupportedImage(byte[] bytes, string mime) => mime switch
    {
        "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/gif" => bytes.Length >= 6 && (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8)
            || bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
        _ => false
    };

    private static string? ReadOptionalString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("GitHub 返回的数据超过允许大小。");
        await using var source = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(block, token);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("GitHub 返回的数据超过允许大小。");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }
}

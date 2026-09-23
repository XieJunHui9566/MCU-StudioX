namespace StudioX.Engine;

using System.Diagnostics;
using System.Text;
using StudioX.Foundation;

/// <summary>通过随包 Git Credential Manager 管理 GitHub.com 的 HTTPS 身份；不保存或记录令牌。</summary>
public sealed class GitHubAuthenticationService(GitRepositoryService git)
{
    private readonly ProcessRunner runner = new();

    public async Task<IReadOnlyList<string>> ListAccountsAsync(CancellationToken token = default)
    {
        var result = await RunAccountCommandAsync(["github", "list", "--no-ui"], TimeSpan.FromSeconds(30), token);
        if (!result.Success || result.OutputTruncated) throw CommandError("读取 GitHub 账号", result);
        return ParseAccounts(result.StandardOutput);
    }

    /// <summary>由 GCM 打开系统浏览器完成 OAuth；返回本机凭据库中的账号名。</summary>
    public async Task<IReadOnlyList<string>> LoginWithBrowserAsync(CancellationToken token = default)
    {
        var result = await RunAccountCommandAsync(["github", "login", "--browser"], TimeSpan.FromMinutes(10), token);
        if (!result.Success || result.OutputTruncated) throw CommandError("GitHub 浏览器登录", result);
        return await ListAccountsAsync(token);
    }

    /// <summary>从 Windows Credential Manager 中移除指定账号；不撤销 GitHub 网站上的 OAuth 授权。</summary>
    public async Task LogoutAsync(string account, CancellationToken token = default)
    {
        ValidateAccount(account);
        if (!(await ListAccountsAsync(token)).Contains(account, StringComparer.OrdinalIgnoreCase))
            throw new StudioXException("GITHUB_ACCOUNT_MISSING", "本机凭据库中没有该 GitHub 账号。");
        var result = await RunAccountCommandAsync(["github", "logout", account, "--no-ui"], TimeSpan.FromSeconds(30), token);
        if (!result.Success || result.OutputTruncated) throw CommandError("移除 GitHub 账号", result);
    }

    /// <summary>供 IDE 内 GitHub REST 操作按请求取用令牌；仅在内存中交给调用方，不经日志或磁盘。</summary>
    public async Task<string> GetTokenAsync(string? account = null, CancellationToken token = default)
    {
        git.RequireCredentialManagerAvailable();
        if (account is null)
        {
            var accounts = await ListAccountsAsync(token);
            if (accounts.Count == 0) throw new StudioXException("GITHUB_NOT_SIGNED_IN", "请先登录 GitHub 账号。");
            if (accounts.Count != 1) throw new StudioXException("GITHUB_ACCOUNT_SELECTION", "有多个 GitHub 账号，请先选择用于当前工程的账号。");
            account = accounts[0];
        }
        ValidateAccount(account);

        // GCM 的 get 标准输出含 password=OAuthToken，绝不能走 ProcessRunner 的日志/诊断管线。
        var start = NewStartInfo(["get"], interactive: false, redirectInput: true);
        using var process = new Process { StartInfo = start };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new StudioXException("GITHUB_GCM_START", "无法启动内置 Git Credential Manager。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var input = process.StandardInput.WriteAsync($"protocol=https\nhost=github.com\nusername={account}\n\n".AsMemory(), linked.Token);
        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        // 诊断也不外传，防止用户启用的 GCM 扩展在错误文本中写出密钥。
        var error = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await input;
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), output, error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new StudioXException("GITHUB_GCM_TIMEOUT", "读取 GitHub 凭据超时。");
        }
        if (process.ExitCode != 0) throw new StudioXException("GITHUB_AUTH_REQUIRED", "GitHub 凭据不可用或已过期，请重新登录。");
        var credential = ParseCredential(output.Result, account);
        return credential;
    }

    private Task<ProcessResult> RunAccountCommandAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token)
    {
        git.RequireCredentialManagerAvailable();
        return runner.RunAsync(new(git.CredentialManagerExecutable, args, git.RootDirectory, timeout,
            SafeEnvironment(interactive: true), RemoveEnvironment: AmbientCredentialVariables()), token);
    }

    private ProcessStartInfo NewStartInfo(IReadOnlyList<string> args, bool interactive, bool redirectInput)
    {
        var start = new ProcessStartInfo(git.CredentialManagerExecutable)
        {
            WorkingDirectory = git.RootDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in AmbientCredentialVariables()) start.Environment.Remove(key);
        foreach (var (key, value) in SafeEnvironment(interactive)) start.Environment[key] = value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private Dictionary<string, string> SafeEnvironment(bool interactive)
    {
        var environment = git.Environment();
        environment["GCM_CREDENTIAL_STORE"] = "wincredman";
        environment["GCM_TRACE"] = "0";
        environment["GCM_TRACE_SECRETS"] = "0";
        environment["GCM_INTERACTIVE"] = interactive ? "true" : "false";
        environment["GCM_PROVIDER"] = "github";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!interactive) environment["GCM_GUI_PROMPT"] = "0";
        return environment;
    }

    private static string[] AmbientCredentialVariables() =>
        System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static IReadOnlyList<string> ParseAccounts(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string ParseCredential(string output, string expectedAccount)
    {
        if (output.Length > 128 * 1024) throw new StudioXException("GITHUB_GCM_OUTPUT", "GitHub 凭据响应过大。");
        string? account = null;
        string? secret = null;
        foreach (var line in output.Split('\n'))
        {
            var field = line.TrimEnd('\r');
            if (field.StartsWith("username=", StringComparison.Ordinal)) account = field[9..];
            else if (field.StartsWith("password=", StringComparison.Ordinal)) secret = field[9..];
        }
        if (!string.Equals(account, expectedAccount, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(secret))
            throw new StudioXException("GITHUB_AUTH_REQUIRED", "GitHub 凭据不可用或账号不匹配，请重新登录。");
        return secret;
    }

    private static void ValidateAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 256 || account.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new StudioXException("GITHUB_ACCOUNT", "GitHub 账号名称无效。");
    }

    private static StudioXException CommandError(string operation, ProcessResult result) =>
        new(result.TimedOut ? "GITHUB_GCM_TIMEOUT" : "GITHUB_GCM_COMMAND",
            $"{operation}失败（退出码 {result.ExitCode}）。请检查网络连接或在浏览器中重新完成授权。");
}

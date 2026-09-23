namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>只使用随软件分发的 Git；不依赖系统 PATH，不改写用户身份和全局配置。</summary>
public sealed partial class GitRepositoryService(string runtimeDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(Path.Combine(runtimeDirectory, "git"));
    public string Executable => Path.Combine(RootDirectory, "cmd", "git.exe");
    public string CredentialManagerExecutable => Path.Combine(RootDirectory, "mingw64", "bin", "git-credential-manager.exe");
    public void RequireAvailable()
    {
        if (!File.Exists(Executable)) throw new StudioXException("GIT_MISSING", "内置 Git 组件缺失，请修复软件安装。开发环境请运行 tools/Prepare-GitRuntime.ps1 后重新编译。");
    }

    public void RequireCredentialManagerAvailable()
    {
        RequireAvailable();
        if (!File.Exists(CredentialManagerExecutable))
            throw new StudioXException("GITHUB_GCM_MISSING", "内置 Git Credential Manager 缺失，请修复软件安装。");
    }

    // URL 级配置可覆盖普通 credential.helper，因此对已验证的远端完整 URL 再清空并指定随包 GCM。
    // 命令级覆盖不改写用户 Git 配置，也不会触发用户装的其它 credential helper。
    internal IReadOnlyList<string> GitHubCredentialConfigArguments(string remoteUrl)
    {
        RequireCredentialManagerAvailable();
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.IndexOfAny(['\r', '\n', '\0']) >= 0
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Length < 2)
            throw new StudioXException("GITHUB_REMOTE_URL", "GitHub HTTPS 远端地址无效，未调用凭据助手。");
        var scope = "credential." + remoteUrl.TrimEnd('/') + ".helper=";
        return ["-c", "credential.helper=", "-c", scope, "-c", scope + "manager"];
    }

    public Dictionary<string, string> Environment()
    {
        RequireAvailable();
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Path.Combine(RootDirectory, "cmd") + ";" + Path.Combine(RootDirectory, "mingw64", "bin") + ";" + Path.Combine(RootDirectory, "usr", "bin") + ";" + System.Environment.GetEnvironmentVariable("PATH"),
            ["GIT_PAGER"] = "cat", ["GIT_EDITOR"] = "notepad.exe", ["GIT_SEQUENCE_EDITOR"] = "notepad.exe",
            ["TERM"] = "xterm-256color", ["LANG"] = "C.UTF-8"
        };
    }

    // 防止 IDE 从另一个仓库的 Shell 启动时继承 GIT_DIR / GIT_WORK_TREE 等定向变量。
    public static string[] AmbientVariables => System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
        .Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray();

    public async Task InitializeAsync(string directory, CancellationToken token = default)
    {
        RequireAvailable();
        // 新建流程在 staging 中执行，指定当前目录创建独立仓库，不误用父目录的仓库。
        if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git")))
            throw new StudioXException("GIT_EXISTS", "目标目录已经包含 Git 仓库，未重新初始化。");
        var result = await new ProcessRunner().RunAsync(new(Executable,
            ["-c", "init.templateDir=", "init", "--initial-branch=main", "."], directory, TimeSpan.FromSeconds(30),
            Environment(), RemoveEnvironment: AmbientVariables), token);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new StudioXException("GIT_INIT", "Git 初始化失败：\n" + result.StandardOutput + result.StandardError);
    }
}

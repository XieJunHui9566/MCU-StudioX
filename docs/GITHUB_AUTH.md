# GitHub 账号接入

MCU StudioX 使用随包 Portable Git 中的 Git Credential Manager (GCM) 2.9.1 完成 GitHub.com HTTPS 身份验证。登录时执行 GCM 的 `github login --browser`，在系统浏览器完成 OAuth；IDE 不收集密码或个人访问令牌。GCM 在 Windows 上使用 Windows Credential Manager 保存凭据，不写入工程、StudioX 设置或日志。

工作台通过 `GitHubAccounts.ListAccountsAsync` 显示已保存账号，通过 `LoginWithBrowserAsync` 发起登录，通过 `LogoutAsync(account)` 移除本机该账号的 GCM 凭据。账号列表仅表示本机存在凭据，不保证 OAuth 授权仍有效。退出是本机移除；如需撤销授权，应到 GitHub 的 Authorized OAuth Apps 页面撤销 Git Credential Manager。

授权后，`GitHubAccounts.GetProfileAsync(account)` 用所选账号的 GCM 凭据读取 `GET https://api.github.com/user`，校验返回的 `login` 与所选账号一致，再返回显示名和头像字节。头像只从 GitHub 头像域以无令牌请求读取，禁用重定向并限制大小；头像不可用时仍返回用户名供界面显示。用户资料和头像不写入工程、配置或缓存文件。此请求只验证当前账号身份，不会自动发起浏览器授权。

GitHub REST 功能每次通过 `GitHubAuthentication.GetTokenAsync(account)` 从 GCM 非交互读取 token，只在请求期间保留于进程内，不写入日志、工程、配置或临时文件。多账号时调用方必须明确选择账号；失败时错误信息不包含 GCM 的凭据输出。用户在组织仓库操作时仍可能需要组织 SSO 授权，或者遇到 GitHub API 的权限拒绝。GCM 2.9.1 的默认浏览器 OAuth 申请 `repo`、`gist`、`workflow` scope；现有旧凭据或 PAT 的实际权限需以 GitHub 响应为准。

GitHub HTTPS 远程 Git 操作显式使用随包 GCM：单次命令先清除普通 helper，再对当前完整远端 URL 清除 URL 专属 helper 并指定 `manager`。Git 的 URL 专属设置可能压过普通 `credential.helper`，因此两层覆盖都需要；它们不修改用户全局 Git 配置。协作页顶部选定账号时，本次 HTTPS Git 命令还会将该账号交给 GCM，不写入仓库配置。SSH 远程仓库继续走 SSH 密钥认证，不使用此账号服务。

本地离线检查：`dotnet run --project tools/StudioX.GitHubAuthChecks/StudioX.GitHubAuthChecks.csproj -- .`。它检查 GCM 帮助命令、账号与凭据输出解析，并用假 HTTP 验证用户资料、头像域名、大小、重定向和令牌隔离；不读取真实账号或执行登录。

依据：

- [GCM 2.9.1 GitHub 命令实现](https://github.com/git-ecosystem/git-credential-manager/blob/v2.9.1/src/shared/GitHub/GitHubHostProvider.Commands.cs)：`list` 仅输出账号名，`login --browser` 保存 OAuth 凭据，`logout` 仅移除本地凭据。
- [GCM 2.9.1 OAuth scope](https://github.com/git-ecosystem/git-credential-manager/blob/v2.9.1/src/shared/GitHub/GitHubHostProvider.cs)：默认 `repo`、`gist`、`workflow`。
- [GCM Windows 凭据存储](https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/credstores.md)：Windows Credential Manager 是 Windows 默认安全存储。
- [Git 凭据协议](https://git-scm.com/docs/git-credential)：`get` 的标准输出包含密码或 OAuth token，因此不得进入一般命令日志。
- [Git URL 专属凭据配置](https://git-scm.com/docs/gitcredentials)：URL 匹配项可决定所用 helper，必须对目标仓库地址单独覆盖。
- [GCM 授权撤销说明](https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/faq.md)：本地删除凭据与 GitHub 端撤销是不同操作。
- [GitHub REST 身份验证](https://docs.github.com/en/rest/authentication/authenticating-to-the-rest-api)：OAuth token 可用于 REST API；凭据无效或权限不足会返回认证或权限错误。
- [GitHub 当前用户 API](https://docs.github.com/en/rest/users/users#get-the-authenticated-user)：`GET /user` 返回已授权用户的 `login`、`name` 与 `avatar_url`。
- [组织 OAuth 应用审批](https://docs.github.com/en/organizations/managing-oauth-access-to-your-organizations-data/approving-oauth-apps-for-your-organization)：组织可能要求所有者审批。

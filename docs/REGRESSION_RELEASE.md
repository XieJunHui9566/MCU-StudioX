# 回归与发行流程

`-LanguageRuntime <已有 runtime 目录>` 将实时诊断可靠性纳入同一回归入口。使用该 runtime 的真实 clangd 验证未保存头文件、连续修正、放弃草稿和 UTF-16 定位，再用独立协议夹具验证旧版本、坏范围、取消、配置加载与编辑竞争、进程退出及恢复限流。协议夹具不是 C/C++ 编译器。随后运行真实 WPF 编辑页面检查诊断列表和波浪线；不会复制或修改安装中的 SDK，也不会连接硬件。可直接使用 `--preview-workspace-editor <新证据目录> <隔离工程> <已有 runtime>` 指定预览所需组件。

tools/Test-Regression.ps1 -OutputDirectory <新目录> 使用 tools/Build.ps1 编译，并检查桌面/服务架构、版本规则、独立插件宿主、离线绘图、SVD 边界、产品工作流及深浅主题 UI。默认不需要固件 SDK，不启动硬件，不改全局 PATH。构建仅还原项目已有 .NET 依赖。

可传 -SamplePlugin 重用示例；否则生成内置 1.0.0 开发插件。显式传 -VendorSvd -ToolsetsDirectory -CoreDumpFixtures 验证官方已保存转储的真实 GDB 解码；加 -F407Pack 执行隔离模板的真实编译。不会自动下载这些输入。实板验收独立于普通 CI。

每步保存日志、退出状态和耗时，regression.json 绑定源码提交及是否有改动；任一失败使总结果失败。UI 使用隔离用户目录、限时进程及截图。regression.yml 在 Windows runner 调用同一入口；release-plan.yml 手动触发版本审核与回归。工作流文件提交后才能远端运行，本地验证不等于远端运行。

可传 `-ProjectHealthNinja <已有 ninja.exe>` 加入工程健康与分析配置故障检查；提供的 Ninja 仅用作隔离夹具的固定版本启动探针。`-LanguageValidationMatrix <JSON>` 加入真实 clangd 的 SDK/目标矩阵。JSON 是数组，每行明确提供 `runtimeDirectory` 和 `projectsDirectory`，后者包含已经配置的独立 ESP 工程。检查包含正确代码、真实错误和修正后的诊断，保留原生数据库哈希；不下载 SDK、不隐式配置或编译这些输入工程。

可传 `-EnvironmentReliabilityNinja <已有 ninja.exe>` 加入组件事务/进程中断恢复检查。它只把已有 Ninja 作为隔离组件夹具内容，不下载 SDK；真实子进程在三个替换边界被强制结束，然后由新应用服务检查记录、校验并恢复。还检查安装目录移动后的相对记录、损坏候选、显式恢复原组件、租约、取消、预览变化和下载缓存。

独立 P0 验收包使用 `tools/New-P0AcceptanceKit.ps1`，明确传入自包含 runner、桌面、插件宿主和相对路径的离线输入计划。它保留产品版本，记录载荷及源码 SHA-256，不创建正式安装器、不下载 SDK、不提交或发布。包内 Windows PowerShell 5.1 一键脚本无需 .NET SDK，可从空目录完成精确版本导入、真实编译、clangd、升级副本、恢复边界与桌面启动；显式恢复夹具保留前次日志。范围和待验收项见 [P0 验收说明](P0_ACCEPTANCE.md)。

迁移验收入口为 `StudioX.EspressifValidation --portability <已有 runtime> <ESP32-C3 包> <STM32F407 包> <新证据目录>`。它复制明确选择的工具版本，隔离进程环境，实际移动工具与工程、备份缓存并编译前后固件，保留日志和内容锁证据；不会连接硬件。此验收不等于干净 Windows 虚拟机或安装器验收。

tools/Invoke-Release.ps1 -ReleaseVersion <已存在版本> -OutputDirectory <新目录> 默认只输出 Plan，不改版本、不打安装包、不提交或推送。

- Verify：执行软件回归。
- Package：要求干净已提交源码、绑定同一提交的通过记录、记录同一干净提交的 payload、固定本地 Inno 编译器，再调用现有安装包脚本。产出 artifact-record.json，关联安装包大小、SHA-256、源码提交和回归记录哈希。
- Publish：要求同样的回归证据、显式 -AuthorizePublish、仓库、安装包、-ArtifactRecord 及可审核的 release notes；校验安装包内嵌版本和产物记录，远端 tag 必须已指向当前提交。创建 draft release，上传安装包、该提交的源码 ZIP、分别列出的 SHA256SUMS 及产物记录。

同版本重打包时，产品版本保持不变，`Invoke-Release.ps1 -ReleaseTag v<版本>-rebuild-<日期>` 使用新发布标签。省略时仍使用 `v<版本>`；该脚本仅核对已有远端标签对应当前提交，不创建或移动标签。基础安装包与完整安装包分别按实际文件名绑定发行证据。

未知发布结果须先检查 GitHub，不能直接重复创建。发行需核对 payload 的工具/器件来源及许可证，见 [INSTALLER.md](INSTALLER.md)。未配置代码签名时不声明已签名。

语法依据：[GitHub Actions 触发文档](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)、[setup-dotnet 官方说明](https://github.com/actions/setup-dotnet)。

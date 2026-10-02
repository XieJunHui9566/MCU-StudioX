# 回归与发行流程

tools/Test-Regression.ps1 -OutputDirectory <新目录> 使用 tools/Build.ps1 编译，并检查桌面/服务架构、版本规则、独立插件宿主、离线绘图、SVD 边界、产品工作流及深浅主题 UI。默认不需要固件 SDK，不启动硬件，不改全局 PATH。构建仅还原项目已有 .NET 依赖。

可传 -SamplePlugin 重用示例；否则生成内置 1.0.0 开发插件。显式传 -VendorSvd -ToolsetsDirectory -CoreDumpFixtures 验证官方已保存转储的真实 GDB 解码；加 -F407Pack 执行隔离模板的真实编译。不会自动下载这些输入。实板验收独立于普通 CI。

每步保存日志、退出状态和耗时，regression.json 绑定源码提交及是否有改动；任一失败使总结果失败。UI 使用隔离用户目录、限时进程及截图。regression.yml 在 Windows runner 调用同一入口；release-plan.yml 手动触发版本审核与回归。工作流文件提交后才能远端运行，本地验证不等于远端运行。

tools/Invoke-Release.ps1 -ReleaseVersion <已存在版本> -OutputDirectory <新目录> 默认只输出 Plan，不改版本、不打安装包、不提交或推送。

- Verify：执行软件回归。
- Package：要求干净已提交源码、绑定同一提交的通过记录、记录同一干净提交的 payload、固定本地 Inno 编译器，再调用现有安装包脚本。产出 artifact-record.json，关联安装包大小、SHA-256、源码提交和回归记录哈希。
- Publish：要求同样的回归证据、显式 -AuthorizePublish、仓库、安装包、-ArtifactRecord 及可审核的 release notes；校验安装包内嵌版本和产物记录，远端 tag 必须已指向当前提交。创建 draft release，上传安装包、该提交的源码 ZIP、分别列出的 SHA256SUMS 及产物记录。

同版本重打包时，产品版本保持不变，`Invoke-Release.ps1 -ReleaseTag v<版本>-rebuild-<日期>` 使用新发布标签。省略时仍使用 `v<版本>`；该脚本仅核对已有远端标签对应当前提交，不创建或移动标签。基础安装包与完整安装包分别按实际文件名绑定发行证据。

未知发布结果须先检查 GitHub，不能直接重复创建。发行需核对 payload 的工具/器件来源及许可证，见 [INSTALLER.md](INSTALLER.md)。未配置代码签名时不声明已签名。

语法依据：[GitHub Actions 触发文档](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)、[setup-dotnet 官方说明](https://github.com/actions/setup-dotnet)。

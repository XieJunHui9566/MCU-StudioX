# 源码版本管理

从 2026-09-22 的独立 IDE 0.1.0 开发版开始建立 Git 基线。基线包含现有编辑器、构建下载、调试和文字串口功能；之后按功能提交，保留可追溯的改动。

- 纳入：`src`、`tools`、示例源码、包生成脚本、文档、许可证与构建配置。
- 排除：`bin`、`obj`、`.build`、`artifacts`、`.artifacts`、缓存、安装包和生成的 `.mcupack`；硬件备份、串口采集与本机开发环境组件留在本地。
- 公开源码仓库为 [XieJunHui9566/MCU-StudioX](https://github.com/XieJunHui9566/MCU-StudioX)，远程名为 `origin`。源码提交与发布版本号分别管理，不因每次提交而自动升级安装包版本。
- 开发前查看 `git status` 并保留未提交修改。只有用户明确要求提交或更新 GitHub 时才创建提交、推送与发布；日常本地开发不隐式同步远程。构建使用 `tools/Build.ps1`。
- 发布使用独立 worktree 和 `codex/` 分支整理源码，通过 Pull Request 更新 `main`。`tools/Invoke-Release.ps1` 将回归、发布目录、安装器与源码 ZIP 绑定到同一个干净提交；已存在发行标签不移动，同版本重打包使用独立重建标签。见 [回归与发布](REGRESSION_RELEASE.md)。
- 恢复旧版本时优先使用单独 worktree 或 `git revert`，避免覆盖未提交修改。
- 临时单片机验证工程及其复制的厂商 SDK 放在 `.artifacts/`，不再将完整生成工程纳入 `examples/`。长期维护的器件配方、插件示例和回归检查源码保留；清理前归档硬件备份及验收证据，见 2026-09-22 清理记录（本地记录）。

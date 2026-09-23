# Git 与工程终端

## 使用

在 IDE 内从器件包新建工程时，自动在工程根目录创建 `.git`，初始分支为 `main`。只初始化仓库，不代填身份、不自动提交。构建目录 `.build/`、`build/`、`cmake-build-*/` 和本地用户文件加入 `.gitignore`；源码、SDK 和 `.studiox/project.json` 可正常提交。初始化发生在工程临时目录内，成功后整体发布；Git 缺失、初始化失败或取消时不留下半成品目标工程。

通过 **工具 → 工程终端**、左侧底部终端按钮或 **Ctrl+反引号** 打开，底部“终端”标签也可进入。终端默认工作目录是当前工程，每个打开的工程保留一个 CMD 会话。`cd`、环境变量和正在运行的命令在切换底部标签后仍保留。

输入框按 Enter 提交一行，支持中文、复制粘贴和上下键历史。粘贴多行会转为空格，不自动执行。输出区域支持选择复制、鼠标滚轮查看历史、Ctrl+滚轮调整字号，继承当前透明背景与主题。点击输出区可以直接发送字符、Tab 和方向键进行控制台交互。

“中断”或 Ctrl+C 发送控制台中断；“结束”终止终端和它启动的子进程，“启动”重新创建会话。关闭工程、切换工程或退出 IDE 会回收旧会话。“私密输入”仅遮挡输入框并跳过命令历史，应在程序询问密码时使用；程序自身是否回显由该程序决定。历史仅保存在当前会话内存中。

## 首次提交

提交前先保存编辑器里的文件。以下命令逐行输入，替换姓名和邮箱；不带 `--global`，只设置当前工程：

```console
git config user.name "你的名字"
git config user.email "你的邮箱"
git status
git add .
git commit -m "初始化工程"
```

配置你自己的远程仓库地址后，可以推送和拉取：

```console
git remote add origin "你的远程仓库地址"
git push -u origin main
git pull --ff-only
```

身份认证使用 Git 原有 HTTPS 凭据管理器或 SSH 机制。软件不会创建远程仓库、代登录或保存自己的额外凭据副本。普通用户无需设置系统 PATH；已有 Git 用户配置照常生效。提交信息默认可用 `-m` 指定，不带 `-m` 时本会话使用记事本编辑。

已有 StudioX 工程和导入的 CubeMX 工程也能使用终端；打开它们不会自动更改现有仓库。若原来没有 Git，可按需执行 `git init -b main` 并自行检查 `.gitignore`。

当前终端使用 CMD 语法，不是 PowerShell；输出保留最近 1,000 行滚动历史及可见屏幕，超出的旧行会丢弃，不写磁盘日志。编辑器保存时检查文件是否被外部修改，防止静默覆盖磁盘内容。

## Git 图谱

打开工程后，从左侧分支图标或 **工具 → Git 图谱** 进入图形模式。顶部可按分支筛选、按提交主题/作者/哈希搜索，并可刷新、获取、快进拉取、推送。主区域显示彩色提交轨道、合并线、分支与标签引用、作者、时间和短哈希；选中提交后，右侧列出更改的文件及差异。先选中一条提交，再按 Ctrl 点击另一条可比较两次提交。

左栏显示未提交文件。选中文件可查看工作区或暂存区差异；支持暂存、取消暂存、输入说明并提交。分支列表支持创建、切换、合并以及安全删除已合并分支；右键提交可复制哈希或在该提交创建分支。仓库设置可为**当前工程**设置提交姓名和邮箱、添加远端。首次推送时，图形模式可选择远端并设置上游；已有上游后使用普通推送。

切换分支、合并或拉取前，如编辑器有未保存内容，IDE 会先询问是否保存。Git 更新工作区后，旧代码标签会关闭，工程树与代码索引重新读取；操作有冲突时显示 Git 的原始错误，保留工作区供用户检查。合并冲突的逐行解决、rebase、reset、强制推送等不在当前图形模式内；高级操作仍可在工程终端执行。打开非 Git 工程不会自动初始化仓库，可在终端明确执行 `git init -b main` 后刷新图谱。

## 实现与发行

- `Engine/GitRepositoryService` 固定使用 IDE 的 `runtime/git/cmd/git.exe`。新建仓库前清除父进程遗留的 `GIT_*` 定向变量，显式初始化当前临时目录，避免误用父目录仓库。
- `Application/GitGraphService` 为图形界面提供仓库快照、提交详情、差异、分支和远端操作；Desktop 不拼接 Git 命令，所有操作仍走内置 Git。文件路径使用参数数组传递，限制在已打开的工程仓库内。
- 图谱的布局与交互参考 [Git Graph 功能说明](https://github.com/mhutchie/vscode-git-graph#features)；该插件的[许可](https://github.com/mhutchie/vscode-git-graph/blob/develop/LICENSE)不授权再分发衍生作品，因此图形绘制、界面和 Git 调用均为本项目独立实现，未纳入插件源码或素材。
- `Application/Terminal/ProjectTerminalService` 管理项目会话；`Foundation/PseudoConsoleProcess` 使用 Windows ConPTY，要求 Windows 10 1809 或更新版本。独立输出线程持续排空；Windows Job 确保结束会话时回收子进程。
- `ConsoleScreen` 实现有限 VT 屏幕、颜色、光标、滚动历史和查询回答；显示快照按版本缓存。界面不直接启动进程。实现面向 Git 和常规命令行，不宣称完整替代 Windows Terminal 的所有终端扩展。
- 从 [Git for Windows 官方发行页](https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5) 获取 Portable Git 2.55.0.windows.5，固定 SHA-256 `5aa8a20f6e9abb2c755f0e73c91c687701a46b309ad84a0ca6509380fa4ae290`。完整组件和许可证保留，构建时运行官方 post-install 后移除本机网络文件副本。来源记录写入 `studiox-provenance.json`。
- 资源准备命令：`tools/Prepare-GitRuntime.ps1`（可通过 `-SevenZip` 指定开发机解压工具）。Git 二进制位于忽略提交的 `artifacts/git-runtime`，构建与发布自动装入 IDE。`Publish.ps1` 和 `Build-Installer.ps1` 检查必需组件。

ConPTY 标准句柄需显式使用 `STARTF_USESTDHANDLES` 和空标准句柄，防止 IDE 从重定向环境启动时继承父进程输入输出；参见 [微软维护者说明](https://github.com/microsoft/terminal/discussions/15814)。关闭流程遵守 [微软 ConPTY 会话文档](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session) 的持续排空要求。

## 本次验证（2026-09-22）

`tools/Build.ps1 -Configuration Release`：零警告、零错误。

`dotnet run --project tools/StudioX.GitChecks -c Release -- <源码根目录> [包含 git 子目录的运行时根目录]` 使用实际内置 Git、真实 ConPTY 和隔离的本地 bare remote 检查新建仓库、中文/空格路径、嵌套仓库隔离、构建忽略规则、失败清理、提交/拉取/推送、目录持续、中文输出、Ctrl+C、缩放、子进程回收、重启以及 VT 显示边界。本次对 IDE 输出目录中的 Git 执行了 20 项检查并通过，未使用外部账号或远程服务。

computer-use 实际界面检查：从器件模板创建 `.artifacts/Git_UI_Test`，确认 main 分支和 `.gitignore`；打开透明工程终端，核对默认目录、Ctrl+反引号入口、结束、重启及关闭工程后的会话清理。终端命令由同一应用服务的测试程序执行；没有通过电脑操控工具输入系统命令。外网 HTTPS/SSH 账号认证未进行联机验收。

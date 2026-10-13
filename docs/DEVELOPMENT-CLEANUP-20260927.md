# 开发中间文件与废弃代码清理（2026-09-27）

按用户要求，清理本轮开发产生的中间文件和已确认无引用的代码。实际删除 **289 项、566,077 个文件，文件逻辑大小共 47,544,485,391 字节（44.28 GiB / 47.54 GB）**，零删除失败。必要测试证据先压缩归档并逐项验证，归档仅 36,071,312 字节（34.40 MiB）。

## 删除范围

- `.artifacts/` 下已结束的生成工程、SDK/开发环境组件副本、调试与界面测试目录。
- `.artifacts/github-publication/stm32-hal-work/` 下的包阶段目录、导入副本和测试工程；保留四个专用脚本与三个验证 JSON。正式候选包、Git 发布仓库及其未提交修改保留。
- `artifacts/installer-payload-0.2.3/`、旧 `MCUStudioX-0.2.2-latest-20260925/` 便携输出，以及已完成的安装器验证目录。
- 22 个被当前发布目录替代的旧器件包/试制目录。当前发布包及维护配方仍需读取的输入保留。
- 旧 Release 输出、各项目 `obj`、验证工具 `bin` 和 Python 字节码缓存。保留已通过构建的 Debug 开发程序。
- `tmp/` 的临时验证项目、两份旧 STC ISP runtime，以及与保留输入 SHA-256 完全相同的 `ch592-source-repro.zip`。

## 废弃代码

- 删除 `MainWindow.xaml.cs` 中无调用和 XAML 绑定的 `ToggleTheme_Click`、`ImportTheme_Click`。现行入口在 `Appearance_Click` / `AppearanceWindow`，主题导入功能保留。
- 删除旧 F407 单型号的 `templates/common/board.h`、`board_clock.c` 和 `debug/stm32f407zg.cfg`。当前 STM32 配方从系列目录读取时钟模板，并按型号生成头文件及调试配置。
- 保留该目录其余六份仍被 STM32、普冉和兆易配方读取的共享素材，并更新其 README。
- 保留所有仍有命令入口的 `MainWindow.*Preview.cs`、回归验证工具及正式器件模板。

两个修改文件的清理前全文保存在 `artifacts/cleanup-20260927/source-before/`，复制后已核对 SHA-256；主窗口文件除上述两个方法外与原基线一致。未覆盖其他未提交修改。

## 保留与验证

- `tools/Build.ps1 -Configuration Debug` 构建通过，**0 警告、0 错误**；日志为 `artifacts/cleanup-20260927/build-after-source-cleanup.log`。
- 当前开发程序：`src/StudioX.Desktop/bin/Debug/net10.0-windows/MCU StudioX.exe`。`obj` 清除后，下次构建会重新生成中间文件。
- 13 个当前发布索引中的 59 个包均存在且 SHA-256 一致，独立 AG32 包保留；五套开发环境组件的 23 个适用核心程序及 clangd、Git、STC ISP、MCP 主机均存在。
- `.artifacts/installer-tools`、`stm32-sources`、CH592 锁定上游压缩包、当前器件包维护输入及正式 runtime 保留。
- `artifacts/hardware-backups`、历史验收归档、现有安装包、最近 F407/CH32V307 RTOS 验收数据保留。CH32V307 试验 BIN 哈希仍为 `407286CB1791CA1F78E8EDC46526A9CD6EA316C36C345FFF672B53E2A00FBF6D`，RP2350 原 Flash 备份仍在。
- GitHub 发布副本 `ide-src` 的原三项未提交改动完整保留。
- 清理目标均在工作区内；先检查链接，再用 PowerShell `Remove-Item -LiteralPath` 删除。289 项目标均已不存在；源码差异检查通过。

## 证据与路径说明

`artifacts/cleanup-20260927/test-evidence.zip` 保存 15,426 份测试日志、截图、结果、必要配置及小源码。每个条目已解压流核对长度和 SHA-256；`evidence-manifest.csv` 保留其原工作区相对路径。

历史验收文档中已删除的 `.artifacts/...` 测试工程路径表示当时的位置，按相同相对路径可在此 ZIP 中查找。特别核对了 CH32V307 RTOS 初始误判/修复拒绝日志，以及两份 AG32 实板会话日志，均已保留。最近 RTOS 实板记录的直接链接仍有效。

完整删除清单、逐项结果、体积统计、Git 状态前后差异及保留依赖检查，见同目录下 `deletion-plan.json`、`deletion-results.json`、`target-sizes.json`、`summary.json`、`git-status-delta.json` 和 `preserved-dependencies.json`。

后续验证继续复用正式开发环境组件；验收后归档必要证据并清除生成项目，避免积累完整 SDK 和发行目录副本。

# P0 独立验收包

这是当前工作区的私有软件验收载荷，保留当前产品版本，不是正式发行安装器。包含自包含 Windows x64 IDE、验收程序、独立插件宿主、clangd、ARM GNU 1.0.0、ESP-IDF 5.5.4 / 6.1.0 离线组件与对应 StudioX 格式 1 器件包。SDK 来自本机已有材料，保留原清单、许可证和内容指纹。

将整个目录复制到可写的 Windows x64 目录，建议 ASCII 路径，例如 `C:\StudioX-P0`。需要约 30 GB 可用空间；原生 SDK 对中文目录仍有限制。无需预装 .NET、Python、GCC、CMake、Ninja 或 7-Zip。验证过程中不需要网络，不改系统 PATH，不接入硬件。

双击 `Run-Acceptance.cmd`。脚本先检查载荷 SHA-256，再从空目录导入明确选择的组件，验证版本缺失提示、重复导入、ESP32-C3 两种 IDF 和 STM32F407 HAL 真实编译、clangd 无误报、SDK 升级副本与原工程保留、组件修复过程强制终止及恢复、下载缓存损坏检查、桌面深浅主题启动。离线组件与编译阶段的时限为 90 分钟；冷环境的全量文件校验可能持续数十分钟。运行期间持有临时电源请求，避免自动休眠，结束时释放，不更改电源计划；手动挂起仍会中断测试。

输出在 `evidence`，包含原始构建日志、工程、锁、恢复记录、备份、截图和机器信息。不会因失败自动删除证据。若失败，阅读同目录的 `acceptance-logs-*`；修正原因后可显式复用同一夹具，前次日志和结果将另存：

```powershell
.\Run-Acceptance.ps1 -ResumeDirectory 'C:\StudioX-P0\evidence\原验收目录'
```

验收成功后直接打开使用该隔离工具目录的 IDE：

```powershell
.\Run-Acceptance.ps1 -ResumeDirectory 'C:\StudioX-P0\evidence\原验收目录' -ShowWorkbench
```

工作台使用新的独立用户数据目录，可以手动打开输出的工程。恢复入口位于“开发环境组件管理 → 恢复未完成操作…”，必须先查看记录，再明确继续或恢复原组件。遇到未知目录或指纹不匹配时保持文件，保留完整诊断。

`cleanWindowsVmTested` 默认保持 `false`：脚本无法证明宿主没有其它开发环境。干净 Windows 实测需另行记录环境、机器和验收结果。本轮交付依据是本机验证；没有硬件验收、正式安装器验收或公开发布授权。

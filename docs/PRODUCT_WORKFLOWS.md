# 调试、故障、构建与分发工作流

用户要求的五个发展方向已接入源码，并纳入 0.2.5.4B 发行。最初的功能验证以 0.2.5.4A 为基线；新版打包及发布验收另见 [0.2.5.4B 更新说明](RELEASE-0.2.5.4B.md)。公共在线目录及发布者签名身份仍需正式配置。

| 能力 | 入口 | 证据与边界 |
|---|---|---|
| 调试连接 | 调试启动、调试连接记录 | 本地/探针/目标/ELF/固件校验/暂停/退出恢复；明确复位模式，取消等待清理 |
| 故障工作台 | 调试 → 固件故障分析 | 暂停 Cortex-M3/M4/M7 状态、ESP Panic 与文件转储；导入数据不继承实板匹配 |
| 构建比较 | 工具 → 构建历史与对比 | 最近 20 个成功快照，静态内存、输入段/符号/文件和 Ninja 编译步骤 |
| 安装体验 | 发布脚本 full/base、软件与组件分发 | 精确版本并存，长度/哈希校验，磁盘预览；IDE 本体继续安装包升级 |
| 生态分发 | 软件与组件分发 | 本地/HTTPS 目录，选定公钥验证，插件更新备份/回退/重新信任，组件源码锁定与回退 |

Desktop 只驱动应用服务。调试阶段来自 Engine 实际检查，连接服务复用原有独占所有者；组件安装持有 BuildService 维护租约，避免与构建互相覆盖。目录读取和插件安装不执行入口程序集。

## 分发制作

`tools/Build-DistributionExamples.ps1 -OutputDirectory <新目录>` 创建开发插件与 MIT 字节助手组件的离线目录。整体仓库尚无明确开源许可证，插件示例目录使用 NOASSERTION，不虚构许可。

`tools/New-ToolsetArchive.ps1 -ToolsetDirectory <已有工具集> -OutputFile <新归档.studioxtools>` 校验完整文件集合后生成归档，不下载 SDK。私人 Supra 许可禁止进入分发。

`tools/New-DistributionCatalog.ps1 -MetadataFile <metadata.json> -OutputFile <同目录/catalog.json> [-PrivateKeyFile <离线RSA私钥>]` 写入归档实际身份、大小及 SHA-256；可生成 RSA-PSS/SHA-256 签名和公钥。私钥不进入目录。

`Publish.ps1` 和 `Build-Installer.ps1` 接受 `-DistributionProfile full|base`，默认 full。基础发布必须指定新输出目录，可用独立 BuildArtifactsDirectory。可传 `-DistributionCatalogDirectory`；复制器仅带入目录、声明归档、签名和公钥，不携带示例构建输出或私钥。打包目录不代表产品安装器已经制作。

## 验证

`tools/Build.ps1` 是基础编译入口。`StudioX.ProductWorkflowValidation` 在新隔离目录检查故障证据、无联网的 HTTPS 模拟下载/签名、组件安装与回退、插件降级保护与回退、MAP/Ninja 解析，以及连接准备失败/取消。

提供现有工具和 F407 包时，额外生成独立 STM32F407ZG HAL 工程：实际编译前后两次、组件参与链接、真实 MAP 和 Ninja 历史、离线 GDB 地址定位及源码变化后的过期拒绝。此验证没有设备连接。

`--preview-product-workflows <新证据目录> <离线catalog.json> <native-history.json>` 在隔离用户目录验证 WPF 页面与主题，输出截图和 result.json。在线服务通过模拟 HttpMessageHandler 验证，不表示已验收公共市场或实际 Core Dump 样本。

故障位依据 [ARM CMSIS core_cm4.h](https://github.com/ARM-software/CMSIS_5/blob/develop/CMSIS/Core/Include/core_cm4.h)，ESP 离线转储流程依据 [ESP-IDF 5.5.4 ESP32-S3 Core Dump 文档](https://docs.espressif.com/projects/esp-idf/en/v5.5.4/esp32s3/api-guides/core_dump.html)和已安装 esp_coredump CLI。故障状态是线索，不从当前 SP 猜测原始异常栈帧。

本轮证据保存在 `artifacts/product-workflows-20261002/validation-summary.json`：54 项去重服务检查、17 项页面检查、28 项调试插件服务回归和 14 项页面回归通过；Release 基础编译零警告、零错误。F407 HAL 实际两次构建的 Flash 静态占用为 1,480 → 1,504 字节。基础发行载荷包含核心运行环境和离线目录，未制作安装器。ESP 转储还未以真实故障样本验收，本轮没有实板连接。

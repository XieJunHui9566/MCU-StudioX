# STC ISP 校准可靠性验证（2026-10-10）

## 结论与边界

同一块 IAP15F2K61S2 / BSL 7.2.5S / 状态 0x70 / CH340C / COM15 上，原始 stcgal 1.10 首轮请求未取得有效帧；只换为官方 `0x66` 脉冲仍失败；同时改用官方的 11 点首轮请求后，两轮校准、切换到 115200 和准备握手通过。受控脚本已将该适配接入普通 ISP 下载及包内 Mon51 制作共用入口。

前三轮实机探针禁止调用擦除、固件写入及选项写入，仅进行了易失校准。随后用户单独授权完整下载同一份 271 字节 LED 程序，修正版完成普通 ISP 擦除、写入、结束及选项保存，退出码 0。已验证校准和完整下载各一次；尚不能证明反复下载可靠性，也不能作为重新制作监控或 IDE 界面的实板验收。用户随后要求替换安装版，下载核心已替换并通过安装后检查，见下文。未升级产品版本、未提交 Git。

## 官方下载记录

用户在官方 AiCube-ISP v6.97C 中下载当前 271 字节 LED 程序，并确认成功。探针观察官方进程的串口 API，未自行打开 COM15 或发送数据；完成后已脱离进程。记录与解码位于 `.artifacts/stc-isp-reliability-20261010/`：

- `official-com15-run1.ndjson`：原始记录，SHA-256 `61C9B2FF621F4968D3BB72FE21234D09447146BADB13369152D173A0C752A25B`。
- `official-com15-run1.analysis.json`、`official-com15-run1.acceptance.json`：24 个 STC15 帧全部通过校验，事件序列无缺口；已完成 TX 937 字节、RX 217 字节。
- 官方的 5 个 64 字节写入块与用户选择的 HEX 一致，末尾为 FF 填充；这项比较不是芯片 Flash 读回校验。
- 端口设置为 2400 / 8E1，校准后切换到 115200 / 8E1。脉冲 API 调用间隔中位数约 15.4 ms；时间戳来自主机 QPC，不能当成 UART 引脚上的位时序。

用户选择的 HEX SHA-256 为 `5C57337B7F7449FFB862E0DAE8D4A911AAAB29FF1B90E9BD828753AECA8DA94B`；解码后的 271 字节 BIN SHA-256 为 `32CE1BB8B41B9471917B05C5888D89E98F6F7E5EDA9742E9E307B78EA57E8F27`。

## 校准请求差异

对照本机 [stcgal v1.10 的协议实现](https://github.com/grigorig/stcgal/blob/v1.10/stcgal/protocols.py) 与官方成功记录，首轮 payload 如下：

```text
stcgal：000c00c080c0ffc000808080ff8000408040ff4000008000c000
官方：  000b00c080c0ffc000808080ff8000408040ff40000080000000
```

官方请求包含 11 个候选值及尾部 `0000` 填充；stcgal 包含 12 个候选值，最后一个为 `C000`。官方校准脉冲为 `66`，stcgal 为 `FE`。

适配只替换精确匹配的首轮 payload 和校准 `FE` 脉冲，保留原同步 `7F`、超时、30 ms 脉冲等待、频率计算、第二轮请求及帧校验。第二轮和传输频率继续由 stcgal 的算法计算，没有照搬官方第二轮的结果。当前成功结果表明本机 30 ms 等待可用，未据此宣称其他串口驱动或 MCU 均可用。

启用条件为实际 `Stc15Protocol`、完整型号 IAP15F2K61S2、代码容量 62464、BSL 7.2.5S、状态字节 45 为 0x70 且当前为内部 RC。其他目标不启用该适配。普通下载仍先执行型号、固件 SHA-256 和时钟门禁；监控制作仍先执行固定编码镜像与目标版本门禁。

## 同板对照

| 请求 | 脉冲 | 实机结果 | 原始记录 |
| --- | --- | --- | --- |
| 原 12 点 | FE | 返回 4 字节，帧起始错误；未进入第二轮 | `calibration-compare2.json` / `.log` |
| 原 12 点 | 66 | 返回 4 字节，帧起始错误；未进入第二轮 | `calibration-pulse66-r1.json` / `.log` |
| 官方 11 点及填充 | 66 | 两轮有效应答、115200 切换 ACK 01、准备 ACK 05 | `calibration-official-r1.json` / `.log` |

第三轮首轮应答为 `000b0c6111d4175b18aa235e2e08332148705d15507770520000`，第二轮为 `000c26c426dd26f1270f2723273747b747e44820484848664893`。计算出的易失用户频率为 11,998,800 Hz；原读到的已保存频率为 11,992,800 Hz。没有写回 RC 校准值或其他选项。探针完成后关闭串口。

两次失败发生在与用户故障相同的首轮校准阶段，但本次返回了无效起始字节，不能称为逐字复现最初的 `pulse timeout`。

## 软件验证

输出目录为 `.artifacts/stc-isp-reliability-20261010/software-r1/`：

- `tools/Build.ps1 -Configuration Release` 完整构建：零警告、零错误；日志 `build-final.log`。
- `StudioX.StcIspValidation`：真实包导入、SDCC 编译、HEX 快照、MCP 门禁及实际内嵌脚本通过；`isp-offline/result.txt`。
- 校准适配 15 项检查：目标范围、短状态拒绝、重复安装、精确首轮替换、保留其他请求与调用参数、同步字节及原错误传播；`isp-offline/calibration-checks.json`。
- 监控制作脚本 11 项检查：共用校准适配、固定镜像、专用 62/42 块及 ACK 失败中止；`monitor-setup-offline2/guard-checks.json`。
- 包内监控与构建流程 30 项检查：目标及资源完整性、所有权拒绝、普通/调试构建切换及源码变化触发最新固件；`monitor-setup-offline2/result.txt`。

第一次完整监控离线流程使用用户当前 LED 源码，测试中旧的 `++app_counter;` 文本替换没有改变程序，导致最后的源码变化检查失败。原日志保留为 `monitor-setup-offline.log`。在独立 `monitor-input` 目录使用该测试所需计数器源码后，30 项流程通过；没有修改用户工程。

## 经授权的完整普通下载

用户确认允许写入并释放 COM15 后，使用从本次 Engine 程序集导出的实际内嵌脚本，下载与官方成功记录完全相同的 271 字节 HEX。不可变输入位于 `software-r1/download-review/`；`review.json` 记录输入 SHA-256、目标、端口、12 MHz 内部 RC、授权及启动时间，`download.log` 保留工具完整诊断，`exit-code.txt` 为 0。

- 日志确认 IAP15F2K61S2、7.2.5S，目标 UID `F44902204670E9` 与官方记录一致。
- 首轮显示 `trials=11`，两轮均显示 `pulse=66`；校准结果 11.987 MHz，115200 切换成功。
- `Erasing flash`、`Finishing write`、`Setting options` 均为 `done`，最终出现 `STUDIOX_PROGRAM_COMPLETE`。
- 写入执行后已关闭串口。该次普通 ISP 覆盖代码 Flash 及可能存在的监控区，重新调试前需按正常流程制作仿真芯片。

工具完成应答不等于 Flash 读回校验，本次没有读回 Flash，也没有以 LED 行为作为运行验收。完整包内监控制作和安装版界面的硬件下载仍未在本次修正后重新实测。

## 安装版替换

按用户“替换安装版”的要求，将经过上述验证的 `StudioX.Engine.dll` 替换到已注册目录 `E:\MCU\MCUStudioX\MCU StudioX`。版本保持 0.2.6.12，修复标识为 `stc-isp-calibration-20261010`；此前终端进度条修复仍保留。未重新打包安装器。

- 替换前检查 IDE 已关闭；旧核心、修复元数据与文件哈希清单备份在 `.artifacts/stc-isp-reliability-20261010/install-r1/previous-files/`。
- 替换前后程序集依赖匹配，6,422 个方法签名和指令代码匹配；对 Regex 源生成器因隔离构建目录改变的私有类型名称散列作归一化。30 个资源中只有 STC ISP 脚本发生变化。
- 只替换下载核心、`repair-build.json` 与 `release-files.sha256.json`。安装后核心 SHA-256 为 `FA91F35C69E3ECB41E1B6A573C7DEB67FBBFF2A5C172232D198CD42078096255`。
- 从实际安装核心提取脚本，其 SHA-256 与已实测脚本一致；使用安装目录中的 Python 执行 15 项校准检查、11 项监控适配检查及型号门禁，全部通过，未打开硬件串口。
- 实际安装 EXE 使用独立用户目录完成启动检查：主窗口初始化、模拟订阅、深浅主题渲染通过，并自动退出。
- 替换后与启动检查后均核对 600 个保护文件的 SHA-256，用户设置、相关工程文件、器件包、工具和其他安装文件保持一致。

第一次替换调用因 PowerShell 向原子替换 API 传入空备份路径而失败，当时原核心未改变。修正为同目录的覆盖移动后替换成功；失败原文保留在 `install-r1/installation-failure.log`。最终记录为 `install-r1/acceptance-final.json`，本次安装检查没有新增硬件写入。

## 残余清理

用户随后要求清理残余。删除了本轮 ISP、终端输出及包内监控安装验证的 35 个明确临时目标，包括重复构建目录、安装载荷、隔离测试工程、任务专用抓包依赖和帮助文件解压副本；共 556,719 个文件、43.52 GiB 逻辑文件大小，磁盘可用空间实际增加 26.53 GiB。没有清理其他历史 STC 调试研究目录、正式发行目录、实际用户工程或已安装组件。

删除前将目标中的 828 份验收报告、日志、截图和测试输入归档，并对 ZIP 中每个文件重新读取核对 SHA-256。归档为 `.artifacts/cleanup-stc-20261010/evidence.zip`，SHA-256 `BDDECE622751A9B87E7659A966EA81D5CDF21EA70BC5938D2C11B216D4DD7920`；`evidence-manifest.json` 将原绝对路径映射到 ZIP 内的工作区相对路径。上文已删除的测试目录表示验收时的位置，相关文件从该 ZIP 中按原相对路径查找。

原始官方抓包、校准对照记录、完整下载快照和日志、安装后验收报告及 `previous-files` 安装备份仍在原位置。`mon51-mcupack-20261009/catalog` 中的器件包也保留。清理前后共核对 717 个保护文件的 SHA-256，安装核心及修复标识、用户设置、相关工程、工具和器件包保持一致。

删除目标的绝对路径逐项限定在工作区 `.artifacts` 的显式清单内，检查目标及内部目录无目录链接；35 个目标均已不存在。清理记录为 `.artifacts/cleanup-stc-20261010/plan.json`、`removed-targets.json` 和 `result.json`。本次未重新构建、连接硬件或修改产品版本。

# AG32VF303CCT6 调试通信排查（2026-09-23）

## 结论与边界

9 月 22 日 IDE 调试失败的首个底层错误是 AGM CMSIS-DAP v2 探针的 USB 数据读取失败，随后才出现 DP IDR、系统总线 / program buffer 读取失败和 GDB 超时。厂商 Downloader 也曾在读取 Flash 时复现相同现象，因此它不是 StudioX 独有的 ELF 校验或目标配置错误。

9 月 23 日做了控制变量对照：同一探针、目标板、调试排线、厂商 OpenOCD 和 1 MHz SWD，经过**有外接供电的 USB 扩展坞**时可完整读取并调试；改为**电脑 USB 直连**后，探针查询和 1 KiB 分块读取可通过，但完整 Flash 读取再次在 `0x80000000` 首报 `error reading USB data`，之后连 DP IDR 也无法读取。这将故障定位为直连条件下持续目标传输时的主机—探针 USB / 探针状态失效。扩展坞同时改变供电和 USB 拓扑，重新接回带外接供电的扩展坞后，完整读回再次成功，形成“扩展坞成功 → 直连失败 → 扩展坞恢复”的 A/B/A 对照。再保持扩展坞接线不变、仅拔掉扩展坞外接电源，完整读回仍成功。这表明外接电源不是成功的必要条件；关键变量是扩展坞的 USB 路径。尚不能确定是直连端口的信号质量 / 时序，还是 AGM 探针固件与主机控制器的兼容性。

## 证据

| 日期、连接 | 只读操作 | 结果 |
|---|---|---|
| 9/21，探针与厂商 OpenOCD | 256 KiB Flash 备份 | 成功。`artifacts/hardware-backups/20260922/.artifacts/toolchain integration/backup.log`；当时的 USB 拓扑未保存。 |
| 9/22，用户当时确认电脑直连 | IDE GDB 校验、64 B 小块读取、厂商 Downloader 读取 | IDE 已识别 `0x40200001` 并停在 `main.c:23`；Flash 读取发生 USB 错误。小块读取约 512 B 后也失败。详见 `docs/AG32-HARDWARE-ACCEPTANCE-20260922.md` 与归档原始日志。 |
| 9/23，带外接供电的扩展坞 `USB\VID_05E3&PID_0610` | 20 次探针信息查询、1 KiB 分块读、256 KiB Flash + 256 B 选项区读回 | 均成功。Flash SHA-256 `A62CE65C122423B84CDBA9115A57A689EE96AE14EEACEDE63F42925B57DD9819`，与 9/21 烧录后的完整读回逐字节相同；选项区 SHA-256 `28ABA0E4BCC6CF8773A6F7C63F23D7F64C0F9F2397F07E1869730CE22AF472C4` 未变。CPU 已恢复运行。 |
| 9/23，带外接供电的扩展坞 | GDB 附加、器件检查、读 PC/SP、分离 | 成功。PC=`0x800003ac`，SP=`0x2001fff0`；OpenOCD 报 `STUDIOX_DETACHED_RUNNING`。 |
| 9/23，电脑直连，设备父节点为 `USB\ROOT_HUB30` | 同样的 20 次查询、1 KiB 分块读 | 均成功，读取后 CPU 恢复运行。 |
| 9/23，电脑直连 | 同样的完整 Flash 读取 | `studiox_check_target` 通过，开始完整读取后首报 `error reading USB data`，继而 DP IDR 失败。OpenOCD 失去响应并已终止；输出文件 0 B，不能作为备份，当时无法确认 CPU 已恢复运行。 |
| 9/23，重新接回带外接供电的扩展坞 | 1 KiB 分块读、256 KiB Flash + 256 B 选项区读回 | 均成功，哈希与切换前相同；CPU 确认恢复运行。 |
| 9/23，同一扩展坞拔掉外接电源 | 20 次探针查询、1 KiB 分块读、256 KiB Flash + 256 B 选项区读回 | 均成功，Flash / 选项区哈希仍相同；CPU 确认运行。故障不能归因于“未给扩展坞额外供电”。 |

9/23 原始脚本、日志及成功的只读备份位于 `artifacts/validation/ag32-debug-investigation-20260923/`。直连失败时的 `flash-direct-readonly.bin` 为 0 B，不可使用。此次没有擦除、烧录或修改选项字节。

## 协议判断

Arm 的 [CMSIS-DAP `DAP_Info` 规范](https://arm-software.github.io/CMSIS-DAP/latest/group__DAP__Info.html)表明该命令查询探针自身，不访问目标 MCU。9/22 的目标访问错误之后，连 `CMD_INFO`、`CMD_LED` 和 `CMD_DISCONNECT` 也曾失败，说明失效已传播到探针 USB / 主控通道。`FW Version = 2.1.0` 是 CMSIS-DAP 协议版本字段，不能当作 AGM 固件构建号；日志另报 `Product Version = 0052`。

[AGM DAP-LINK 手册](https://www.agmcn.com/doc/6878.html)说明 J4 / nRESET 控制 Auto 与 Blaster 模式，J3V 可影响目标供电，D4 快闪表示 DAP 模式。[AG32 硬件说明](https://www.agmcn.com/doc/6846.html)要求 VDD/VDDA 为 3.15–3.45 V，并提醒关注纹波。上述资料提供排查方向，不能单凭日志推断哪一项硬件条件失效。

## 软件修正

StudioX 原先把 `monitor verify_image` 的任何 GDB 命令错误都误报为固件不一致。现在依据当前会话的原始日志区分 USB 传输错误、明确的字节差异及证据不足，避免误导用户重新烧录。另修正 GDB 生成 `__pycache__` 文件后触发开发环境组件完整性拒绝的问题：仅容许与已索引 GDB Python 源文件匹配的标准缓存，应用启动的 GDB 将缓存放到开发环境组件目录之外。现有 AG32 开发环境组件的 11,576 个索引文件完整校验、23 项开发环境组件回归、AG32 完整离线验证与 Release 全量构建均通过。

# IAP15L2K61S2 实板验收（2026-10-21）

用户在软件适配后接入实机，明确允许编写测试程序并下载、覆盖废弃旧程序，并配合操作下载/电源开关。本次授权及验收仅属于当前 IAP15L2K61S2 板，不扩展到其他型号或保护配置修改。

## 识别与固件

- CH340 / COM15；只读 ISP 识别准确型号 IAP15L2K61S2、61 KiB Flash、ISP 7.2.5S、状态尾字节 70。
- 原程序按用户要求覆盖。先以独立 L 镜像制作 Mon51，再由用户断电约 2 秒重新上电，原生握手返回 V2.5。
- 制作 BIN 共 61440 字节，源 SHA-256：`D2320E17FE8961813D8A012C46571390D6AA3BA683A594DD05697AD5B990D729`。专用 62/42 写入及完成应答成功；写回 ISP 读取选项与校准结果，未主动指定时钟来源或新 RC 频率。L 使用原有 stcgal 校准流程，没有套用 F 的专用 66/11 修正。
- 216 字节测试程序仅操作 DATA/XDATA 计数变量，不配置板级 GPIO。SDCC 在隔离的准确型号工程中编译，Mon51 用户区更新后完整回读。
- 测试 BIN SHA-256：`3CF1A2CF77288C0C9FD4F3D4AD28B35C9B54EB10E9FF8774B61F2E52E95D9FE4`。

## 实际结果

16 项实机检查通过：握手与硬件会话身份、保持暂停时的用户程序完整回读及 CDB 加载、实时 CODE 哈希、原生停止状态、源码断点、19 项寄存器与 checkpoint/main 调用栈、源码单步、跳出、条件断点中的真实循环、XDATA 编辑恢复、断点原字节完整恢复、正常退出后重连、串口释放与测试程序恢复运行。其中四次等待分别核对不同执行阶段确实处于停止状态。

首次断点处 counter=0、total=0；源码单步后 counter=1、total=0；跳出后 total=1；条件断点 counter>=3 停止时 counter=3、total=6。原始 HEX、CDB 和当前源码指纹均由构建回执核对，未把旧 F 固件地址或源码行当作 L 的实板事实。

结束时断点修改字节已恢复，热连接再次读取的 CODE 哈希仍等于所选 BIN；最后释放 COM15，保留最小计数程序运行。未验收其他 ISP 版本/状态、其他时钟配置、其他板型、外设时序或用户工程。

## 原始证据

证据目录 `.artifacts/stc-iap15l2k61s2-20261021/hardware/`：

- `isp-identification.json`、`isp-identification.log`：只读识别。
- `hardware-plan.json`、`prepare.log`、`test-project/src/main.c`：所选镜像、程序和实际构建。
- `setup-attempt-1.log`、`monitor-setup-report.json`、准备目录中的 `stcgal.log`：真实制作。
- `hardware-validation-attempt-1.log`、`hardware-result.json`：16 项检查和通信日志路径；通信日志记录主机 UTC 时间，非芯片时间戳。

本轮修改 IDE 本体的 Engine、Application 与 Desktop，并生成 `stc.stc8/0.1.6`；接口与镜像边界见 [适配说明](STC-MON51-IAP15L2K61S2.md)。产品版本未升级，GitHub 与现有安装版未自动更新。

# F407 / ST-Link 实机界面验收（2026-09-21）

工程：`.artifacts/F407-Hardware-Debug`。程序使用外部 8 MHz、168 MHz 系统时钟，只运行计数与函数调用，不配置外部 IO。
软件：`artifacts/MCU StudioX Debug Preview/MCU StudioX.exe`，独立 IDE 0.1.0 预览。

用户授权下载该最小示例后，下载、调试、断点、单步和结束操作均通过 computer-use 的真实 IDE 窗口完成。后台仅用于构建 IDE、修复代码、检查日志和运行离线回归，不以命令行连接代替界面验收。

## 设备与备份

- ST-Link V2，V2J46S7；SWD 请求 2000 kHz，实际 1800 kHz。
- Cortex-M4，DBGMCU ID `0x10076413`，Flash 1024 KiB，6 个硬件断点、4 个观察点。
- ID 能确认 F405/F407 类别，不能单凭 ID 确认封装丝印。工程目标按用户提供的 F407ZG。
- 原 Flash 备份：`.artifacts/hardware-original-backup/stm32f4-1MiB-20260921-215352.bin`，1,048,576 字节。
- SHA-256：`C789BF8BF62CF47F954491866C7C72103C16D9875FC62578E04EFE0B4E874617`。
- 未写选项字节、OTP，未整片擦除。

## 已通过的界面操作

| 项目 | 实际结果 |
| --- | --- |
| IDE 编译、下载 | 编译退出 0；写入示例并校验成功、复位运行 |
| 固件不匹配拒绝连接 | 中文提示先下载，恢复目标运行并释放探针 |
| 实机附加 | 显示 ST-Link / STM32F407，读取真实 PC、内核/FPU 寄存器和调用栈 |
| 普通断点 | 鼠标点击 main.c:24 边栏；运行后停在该行，PC `0x080001c0` |
| 单步进入 | ComputeOutput:13，参数 input=1；显示两层调用栈 |
| 逐过程 | 停到第 14 行，doubled=2，变化值高亮 |
| 跳出 | 返回 main:24，r0=3；再次逐过程后 app_output=3 |
| 只读内存 | 从 0x20000000 读取 64 字节，app_counter/input/output 对应 1/1/3；SystemCoreClock=168000000 |
| 临时断点 | 右键设置，触发暂停后边栏标记及断点列表条目删除 |
| 一次性日志断点 | 第 25 行输出 `count=2, output=5`，随后自动继续，断点删除 |
| 运行中结束 → 下载 | 22:20 运行中点击结束，正常 detach，随后直接下载成功；没有重新上电或插拔 |
| 复位与运行到光标 | 最终版复位显示 Reset_Handler:61、PC=0x080001e4；右键运行到 main:25 后 counter/input/output=1/1/3 |
| 暂停结束 → 下载 → 重连 | 最终版同一窗口内连续操作成功；重连运行再次命中 main:24，counter=297、input=41、output=81；全程没有断电或插拔 |

## 实测发现并修正

1. extended-remote 的 detach 成功响应并不代表 TCP 已关闭。结束时再退出 GDB，等待 OpenOCD 确认 `STUDIOX_DETACHED_RUNNING`，再回收子进程和探针锁。启动失败同样完成清理，保留原始错误。
2. OpenOCD 校验算法会使用 RAM 工作区，曾污染全局变量。调试会话禁用工作区，改为主机读回校验；再次附加时读取到 counter/input/output=121/121/243，随后复位运行得到 1/1/0，单步计算正确。
3. monitor 复位不会通知 GDB 清除旧寄存器/栈缓存。复位后显式刷新寄存器、栈与内存缓存再读取快照。
4. 失败状态再次点击调试会直接重新尝试；内存窗口在新快照到来后要求重新读取。

## 证据

- 固件不匹配后的正常释放：`.build/download-51434fe9428a4e28a911d463d49697a7/debug-session.log`。
- RAM 修复后的普通/临时/日志断点、单步、运行中结束：`.build/download-426572e55be9429392a928a7078c66b7/debug-session.log`。
- 运行中结束后的下载：`.build/download-7c19691fee8b4a6dbd8702da884bba77/openocd.log`。
- 最终版复位/运行到光标/暂停结束：`.build/download-58122ec4d6124810b16dff012ba939f8/debug-session.log`。
- 最终版暂停结束后的下载：`.build/download-40a74650ec80401997b49bea407a18ca/openocd.log`。
- 最终版同窗口重连并再次命中断点：`.build/download-5e0d95b174334382924b20591f9186e1/debug-session.log`。
- 上述 `.build` 路径均相对于验收工程。
- IDE 发布构建：`.artifacts/hardware-debug-publish-4.log`。
- 离线状态机与特殊断点回归：`.artifacts/hardware-offline-regression-2.log`，全部通过。

## 验证边界

本次计算机操作工具可以点击断点对话框，但向该模态窗口输入条件文本未成功。因此，带自定义条件和忽略次数的组合仍只有离线回归结果，不记为实机界面通过。未验证拔线恢复、RTOS 线程、其他 STM32 型号或其他调试探针。寄存器写入、SVD 外设视图、反汇编不在此次实现范围。

交接时最终预览窗口保持打开，实机暂停在 main.c 第 24 行，可直接单步或点击「结束」释放探针。板上已是用户授权的最小示例，原固件仅备份，未自动恢复。此次更新的是调试预览目录与源码，未重新发布对外安装包。

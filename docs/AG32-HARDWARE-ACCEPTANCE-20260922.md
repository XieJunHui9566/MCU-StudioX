# AG32 官方烧录器实机验收记录

用户于 2026-09-22 明确要求开始验收，并确认设备已接好。本次使用 `artifacts/MCU StudioX Debug Preview/MCU StudioX.exe` 与 `.artifacts/AG32-Debug-Review` 最小工程。

## 当前结果：能识别芯片，固件读取时通信中断，尚未完成验收

最后一次兼容选项对照也失败：机身复位后，`adapter init` 成功，随后设置 quirk 得到 `CMSIS-DAP quirk workarounds enabled`，没有再发生初始化前设置时的崩溃；但初始化目标时 `CMD_CONNECT` / DP IDR 失败，随后 `CMD_DISCONNECT` / `CMD_LED` 也失败，退出代码 1。日志 `target-read-quirk-after-reset.log`。确认没有遗留 OpenOCD / GDB 进程，没有写入 Flash、逻辑区或选项字节；由于通信中断，不能确认目标 CPU 当前已恢复运行。

当前结论：基本探针查询可稳定完成，访问目标芯片时可复现通信异常，且官方 Downloader 也复现。关闭厂商 GDB 扩展和初始化后启用 quirk 均未解决问题；这些实验配置没有加入产品或器件包。尚无法在调试线/目标电气连接与厂商固件/驱动的传输处理之间确定原因，不能宣称 AG32 实机调试适配通过。停止相同条件下的反复复位重试，保留日志供后续硬件替换对照或厂商核查。

供电调整后的重试已识别 AG32VF303 的 ID `0x40200001`、256 KiB Flash 和 RV32 内核，GDB 已连接并暂停在 main.c 第 23 行。器件/逻辑区检查通过，但 `monitor verify_image ... elf` 读取应用时发生 USB 读错误，随后系统总线和 program buffer 读取失败，20 秒后会话超时。**不能将这次连接计为调试验收通过，也未确认断线后 CPU 恢复运行。**

100 kHz 的后续界面重试仍失败于探针 `CMD_INFO`，没有再次连接 CPU。已请求用户保持调整后的供电方式、重插烧录器 USB。原因仍待区分：供电/USB 链路不稳，或批量读取触发探针异常。本次没有烧录、擦除或修改选项字节。

新增日志：`.artifacts/ag32-hardware-acceptance-20260922/after-power-adjustment.log` 与 `retry-100khz.log`。后续小块读取及官方工具对照见下文；当前没有取得本次板上固件的完整备份。

## 再次重试：小块读取、官方手册与官方界面对照

用户已确认 USB 电脑直连，没有经过 HUB。只读诊断使用 `backup-small-reads.cfg`，设置 100 kHz，探针报告实际时钟 194 kHz。首次读取 64 字节成功，随后按 64 字节循环保存，写出 7 块共 448 字节后，在 `0x800001c0` 再次发生 USB 读错误。加上最初重复读取的 64 字节，约在 512 字节数据读取后中断；这仅是本次观察，不能认定固定故障边界。小块读取也失败，因此不能将原因仅归结为 IDE 的整段 ELF 校验。日志为 `backup-small-reads.log`，`flash-before.words` 只是部分读取结果，**不可当作完整备份**。退出时无法确认 CPU 恢复运行，已终止本次失去响应的 OpenOCD 进程。

对唯一匹配的 AGM USB 设备调用 libusb 软件复位返回 0，但重新查询仍报 `CMD_INFO failed`，不能视为烧录器固件已重启。厂商 OpenOCD 的 `cmsis-dap quirk enable` 诊断直接崩溃（`0xC0000005`），没有将此选项加入项目或器件包。日志为 `usb-software-recovery.log`、`quirk-probe.log`、`after-usb-reset.log`。SDK 与内置工具的 OpenOCD、libusb、hidapi 哈希一致。

按照用户要求查阅 [AGM DAP LINK 使用说明](https://www.agmcn.com/doc/6878.html)、[AGM BLASTER 使用说明 PDF](https://aimg8.dlssyht.cn/u/2213600/ueditor/file/1107/2213600/1693893693740873.pdf) 及 [海振远 AGM DAP LINK 技术手册](https://cdn.yun.sooce.cn/4/70277/pdf/1734309508819abc1cc9c8f99ef1135a86209e6412ab2.pdf)，发现需核对的设备特性：

- Auto 模式根据 nRESET 引脚识别 DAP/USB Blaster；J4（旧版 JGND）短接或 nRESET 接地可选择 Blaster。手册标注 D4 快闪为 DAP、慢闪为 Blaster；静态照片不能判断闪烁状态。
- 机身 SW_NRST 是烧录器主控的复位键；SW_IO 是离线下载键。两者作用不同。
- Downloader 的 Offline 页包含 Auto、DAP、Blaster、DAP+GDB、Auto+GDB 设置。页面初始显示 Auto，但没有读回确认，不能当作设备当前设置；GDB 名称本身也不证明普通 OpenOCD 调试必须开启该模式。
- AG32 的 CMSIS-DAP 使用 TCK/TMS 两线 C-JTAG，官方 OpenOCD 配置仍以 SWD transport 驱动其专用桥接；不应仅凭引脚标签改成通用四线 JTAG。

使用 Computer Use 打开本机 SDK 的 `packages/tool-agrv_logic/bin/Downloader.exe`，选择 CMSIS-DAP，点击 **Query Device ID**。官方界面实际调用其自带 OpenOCD 与完整 `agrv2k.cfg`（4 MHz、CONNECT_UNDER_RESET），同样报 `CMSIS-DAP command CMD_INFO failed`，退出代码 1。此结果说明故障可在官方工具中复现，尚不能区分探针固件状态、USB 链路或驱动实现原因。仅查询设备 ID，没有点击 Download、Update Offline File、Update DAP Settings 或运行高级页擦除命令。已请用户短按机身复位键并报告 D4 状态，待通信恢复后继续。

## 机身复位后的官方界面复测

用户短按机身复位键后报告 D4 快闪。通过 Computer Use 在官方 Downloader 重新选择 CMSIS-DAP，点击 Query Device ID，成功得到 `0x40200001`、256 KB Flash，界面提示 `done with code 0`。探针 FW `2.1.0`、Product Version `0052`。说明机身复位可以恢复基本通信，但仍需验证连续传输。

随后通过官方 Advanced 页选择 **Read flash**（含空白数据），保存到 `.artifacts/ag32-hardware-acceptance-20260922/official-flash-after-probe-reset.bin`，按 Run。界面调用厂商 OpenOCD：4 MHz、CONNECT_UNDER_RESET、reset init、agrv download clone、verify。读取选项区 128 字节成功后，在 Flash `0x80000000` 读取处出现 USB 错误，继而 sysbus/progbuf 读取失败、DP IDR 失败。通过官方界面 Stop 停止，界面提示 `terminated by user`，无遗留 OpenOCD。输出文件仅 0 字节，**不是可恢复的备份**；没有执行下载或擦除，CPU 当前运行状态尚不能确认。

用户随后报告 D1 亮。LED 不能替代本次命令结果；当时请求暂时断开目标板调试排线、保留 USB 并复位，以隔离探针与目标通信。另通过厂商 OpenOCD 自带命令离线查询，`cmsis-dap-gdb mode` 默认返回 `on`，其 `off` 可作为进程内对照设置，后续结果见下文；没有修改烧录器持久设置或固件。

## 探针与目标板隔离对照

用户确认断开调试排线并复位后，运行 `probe-only-info-native-off.cfg`，只执行 adapter init 和探针 info，不创建或访问目标。先 20 次，再分别以 GDB 扩展 on/off 连续查询 200 次，均完成、退出代码 0。两组 200 次之间没有再次复位或改线。日志为 `probe-only-info-native-on-200.log` 和 `probe-only-info-native-off-200.log`。关闭扩展后日志不再出现 Native debug features。此结果仅说明该负载下基本 USB/探针查询稳定，不能排除目标访问时的传输问题。

用户确认接回同一块开发板并供电后，运行 `target-read-native-off.cfg`（1 MHz、进程内关闭厂商 GDB 扩展）。DPIDR 和 datacount 可读，但随后的 CPU examination 即发生 USB 错误，随后不能退出 LED/DISCONNECT 命令，退出代码 1，没有生成完整备份。关闭扩展不是当前已验证的解决办法。日志 `target-read-native-off.log`。

尝试通过 Windows PnP 重启精确匹配的 AGM USB 父设备，系统返回 Access is denied；没有重启 USB 控制器或更改驱动。再次查询探针仍 CMD_INFO 失败。准备 `target-read-quirk.cfg`，在 adapter init **之后**设置 quirk（此前初始化前设置会崩溃）。该脚本首次执行止于 adapter init/CMD_INFO，并未执行到 quirk；用户再次按复位后执行到 quirk，但连接目标仍失败，见本文当前结果。没有把实验设置加入发行包。

## 最初的 USB 识别阻塞

最初两次连接尝试均通过 Computer Use 点击真实 IDE 的“调试”按钮发起，没有用命令行连接成功代替界面验收。

1. Windows 枚举到 CMSIS-DAP v2，接口 `USB\VID_CAFE&PID_1001&MI_00`，服务 WINUSB；同一复合设备包含 COM24。父设备序列号与早期 AG32 测试记录一致。
2. 默认 CMSIS-DAP 自动识别失败：`could not read product string for device 0xcafe:0x1001: Pipe error`，随后 `unable to find a matching CMSIS-DAP device`，OpenOCD 退出代码 1。
3. 临时明确指定已枚举的 VID/PID、USB bulk 和接口 0，通过 IDE 再试。OpenOCD 找到接口，但基本 `CMD_INFO` 查询出现 `error reading USB data` / `error writing USB data`，仍退出代码 1。没有取得探针能力或芯片身份。
4. 未发现遗留 OpenOCD / GDB 进程占用；清理完成。明确接口没有解决问题，已撤回临时强制参数，源配方和测试工程恢复默认配置，不将实验参数加入发行包。
5. 已请求用户重插 AGM 烧录器 USB，待恢复后继续原验收。界面保留测试工程并增加 `app_heartbeat` 观察项，尚无真实变量值。

最初两次没有执行下载、擦除、选项字节写入或目标调试命令；当时 CPU 尚未连接。后续供电调整后的进展见本文“当前结果”。断点、寄存器或退出恢复功能尚不能计为通过。

## 原始日志

- 默认连接：`.artifacts/AG32-Debug-Review/.build/download-1f15e508c5844bb491e7ec38d600b9fc/debug-session.log`
- 明确接口诊断：`.artifacts/AG32-Debug-Review/.build/download-b8b77915704d453d87023539c89ac504/debug-session.log`
- 副本：`.artifacts/ag32-hardware-acceptance-20260922/usb-default.log` 与 `usb-explicit-interface.log`。

恢复后仍需通过 IDE 验收：芯片及固件核对、普通和特殊断点、暂停/运行/单步、寄存器/变量/调用栈/内存，以及不重新上电的“结束 → 下载 → 再调试”。下载前保留当前板上固件及逻辑区/选项字节备份，使用已选定的 AG32 最小测试工程。

接口诊断依据：[OpenOCD CMSIS-DAP USB bulk 源码](https://github.com/openocd-org/openocd/blob/master/src/jtag/drivers/cmsis_dap_usb_bulk.c)，以及本机厂商 OpenOCD 的 `help cmsis-dap`。USB 通信异常的确切原因（固件状态、线缆或 USB 链路）尚未确定。

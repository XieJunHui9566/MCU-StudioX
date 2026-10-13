# CH592F 实板调试状态（2026-09-24）

**状态：当前这套实板暂时无法调试。** WCH-LinkE 能被电脑识别，但无法与目标芯片建立两线握手；尚未进入读取芯片 ID、下载或 GDB 调试阶段。原因未确认，等待沁恒技术支持。此结论只针对下述实板和接法，不代表 CH592 系列本身不支持两线调试。

## 实物与接线

- 芯片：WeAct Studio V1.0 模块上的 **CH592F**。模块背面标注 `B15→TCK、B14→TIO`，板载 BOOT 键连接 PB22。
- 探针：WCH-LinkE，电脑枚举为 `WCH-LinkRV`（USB VID `1A86`、PID `8010`），OpenOCD 报 RISC-V 模式固件版本 **2.16**。
- 接线：探针 `CLK0→PB15`、`DIO0→PB14`、`GND→GND`。照片可见对应位置；用户以万用表确认三线导通。
- 供电：分别试过模块 USB 单独供电和探针 3V3 单独供电，均保持共地，没有同时连接两路 3V3。探针供电时，模块 3V3-GND 测得 **3.3 V**。

## 已做的只读检查

1. WCHISPStudio V4.00 经模块 USB Boot 搜索，识别为 **CH592**，日志显示“开启调试接口成功”。随后释放 BOOT 并重新上电。
2. 使用 MounRiver Studio 2 随附的厂商 OpenOCD `0.11.0+dev-snapshot (2026-08-25-16:45)`、原厂 `wch-riscv.cfg`，在 400、4000、6000 kHz 均于 `init` 阶段失败：`WCH-Link failed to connect with riscvchip`。尚未读到 `R8_CHIP_ID`。
3. 在仅探针供电时，按 BOOT 并重新上电，以及按 BOOT 后复位的短时间重试，仍未握手成功。未完成“模块 USB Boot 已被 WCHISP 确认连接时，同时运行 WCH-Link”的测试。
4. 未通过本次工作擦除或烧录芯片；未自动解除读保护或修改其他选项字。用户只在官方 WCHISP 中执行过“开启调试接口”。

## 请技术支持协助确认

- WCHISP 显示“开启调试接口成功”后，能否在 **USB Boot 下不擦除用户程序**，只读查询 CH592 的 `CFG_DEBUG_EN`、`CFG_ROM_READ`、`CFG_RESET_EN` 实际状态？这些状态是否还需另行设置才能连接 WCH-LinkE？
- WCH-LinkE RISC-V 固件 2.16 与 CH592F 的两线调试是否兼容，是否有最低版本或特殊复位/上电时序要求？
- 在接线和 3.3 V 已确认的条件下，如何进一步区分探针通道故障、芯片调试脚故障与目标选项配置问题？

## IDE 软件适配范围

`wch.ch592/0.1.0` 包的 CH592D/F/X 均已用 WCH GCC 12、CMake、Ninja 实际编译，生成 ELF/BIN/HEX/MAP；OpenOCD/GDB 参数和只读保护检查通过离线验证。**实板下载和源码调试未通过，不能作为已验收功能。** `wch.ch595/0.1.0` 也已完成三型号离线编译，但本次硬件是 CH592F，不能用它验证 CH595。

参考：[WCH-LinkE 使用说明](https://www.wch.cn/uploads/file/20250122/1737510981191225.pdf)、[CH592 数据手册](https://www.wch.cn/downloads/CH592DS1_PDF.html)。

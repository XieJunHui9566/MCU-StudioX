# CH32V203C8T6 下载与调试验收（2026-09-23）

CH32V203C8T6 + WCH-LinkE 已通过最小固件下载和 MCU StudioX IDE 基础调试验收。使用此前完成的 `wch.ch32v203/0.1.0` 器件包和 Release IDE，本次无需修改调试代码。结果仅覆盖这块 C8T6 板卡，其他 10 个收录型号仍为编译和离线验证。

## 环境与授权

- 芯片 ID `0x20310500`，64 KiB Flash / 20 KiB SRAM；WCH-LinkE 固件 2.16，SDI 4000 kHz。
- 开发环境组件 `wch.riscv/1.0.0`，厂商 OpenOCD 0.11.0+dev-snapshot（2026-08-25）和 WCH GCC/GDB。
- 原板 RDPR 为 `0x00ff`，FLASH_OBR 为 `0x03fffcfe`，读保护已开启，无法备份原程序。用户明确同意解除读保护并表示原程序无用，同时授权写入已编译的最小测试固件进行调试。
- 一次性解除脚本按本机官方 SPL 的 `FLASH_Unlock()` 和 `FLASH_ReadOutProtection(DISABLE)` 顺序执行：先核对精确芯片 ID、保存 16 字节选项配置，再将 RDPR 改为 `0x5aa5`。其余 USER、DATA、WRP 字节保持原值，回读确认；FLASH_OBR 变为 `0x03fffcfc`。原 Flash 随解除保护被清除，不能声称已备份。
- 此授权没有变成 IDE 自动解锁功能；器件包的保护检查继续拒绝读保护目标。

## 固件与下载

工程复用 `.artifacts/ch32v203-validation-final/CH32V203C8T6`，没有新建用户工程。程序执行 `System_Init()` 后进入空循环，使用内部 HSI / PLL 144 MHz，不初始化板级 GPIO、串口或定时器。

固件长度 1044 字节，SHA-256：

```text
67a07c5d52502c204a063acbaa6cb68f52857cb3acaad40d4cc7c72b9d2bcbea
```

通过器件包中的 WCH-Link 接口与目标脚本，以明确 page erase 模式下载；写入前核对型号/读保护，写入后执行 `verify_image`，并独立回读物理地址 `0x08000000` 起 65536 字节。前 1044 字节与授权固件逐字节一致，复位并恢复运行成功。

厂商 OpenOCD 在下载阶段输出一次 `Unsupported register (enum gdb_regno)(8)`，但返回成功；不能仅凭退出码忽略该诊断。本次以独立物理 Flash 回读、IDE 中重新核对 ELF，以及实际读取整数帧指针 `fp` 确认本次下载和调试成功。原始诊断保留在 `download.log`，未屏蔽。

## IDE 实际操作

| 操作 | 实测结果 |
| --- | --- |
| 附加与固件校验 | 校验 1044 字节 ELF 后停在 `main.c:7`，PC `0x166` |
| 寄存器 | 实际读取 39 项整数/基础机器寄存器，不请求 FPU；`fp` 作为整数帧指针正常读取 |
| 硬件断点 | 在 `main.c:5` 设置硬件断点，地址 `0x164`；复位后运行命中 |
| 复位 | 暂停在 `_start`，PC `0x0` |
| 单步进入 | 从 `main` 进入 `System_Init` 第 6 行 |
| 逐过程 | 执行优先级分组调用后停在第 7 行 |
| 嵌套调用 | 再次进入 `SystemCoreClockUpdate`，显示该函数、`System_Init`、`main` 三层调用栈 |
| 局部变量 | 读取 `tmp`、`pllmull`、`pllsource` 的类型和值 |
| 跳出函数 | 收到 `function-finished`，返回 `System_Init` 第 8 行 |
| 变量观察 | `SystemCoreClock = 144000000` |
| 只读内存 | 从 `0x20000000` 读取 64 字节 SRAM；`0x20000010` 对应时钟全局变量 |
| 运行与暂停 | 从初始化处继续，用户暂停后定位主循环 `main.c:7` |
| 暂停时结束 | 删除断点、detach，收到 `STUDIOX_DETACHED_RUNNING`，释放探针 |
| 不断电再连接 | 重新附加并校验固件成功，停在同一主循环 |
| 运行时结束 | 自动中断后删除断点并 detach，再次确认恢复运行 |

两轮 IDE 调试日志没有 GDB `^error` 或 OpenOCD `Error:` 记录。最后独立只读会话再校验程序、回读 Flash 与选项字节，确认目标处于 running 后关闭；无残留 OpenOCD / WCH GDB 进程。

## 回读证据

所有证据保存在 `artifacts/ch32v203-hardware-20260923/`，不纳入源码版本管理：

- `authorized-unlock.cfg`、`unlock.log`、`options-before.bin`、`options-after-unlock.bin`：用户授权解除及配置保留记录。
- `authorized-firmware.bin/.elf`、`download.cfg`、`download.log`：精确固件和下载过程。
- `ide-session-1.log`：完整 IDE 断点、单步、变量、内存和暂停时结束；06:10:06 确认恢复运行。
- `ide-session-2.log`：重新附加及运行时结束；06:11:11 确认恢复运行。
- `flash-after-download.bin` / `flash-after-debug.bin`：65536 字节，内容一致，SHA-256 `f95ab6b242102679e7890a38cc9c77aa8967b0b3d3bfd29b87a76d6342de18d9`。
- `options-after-download.bin` / `options-after-debug.bin`：16 字节，内容一致，SHA-256 `d445910d68bd533b5885e81352c14c9ebf824826dbd867026dcbc1a3e3f90718`。
- `final-readback.log`、`debug-readback-comparison.json`：独立校验及最终恢复运行。

## 验收边界

未测试其他封装、非 E 型探针、其他速率下的完整调试、多探针、USB 拔插恢复、RTOS、复杂优化代码或中断嵌套运行。V203 的 24 项中断目标代码检查仍属于此前的软件验证，不应视作本轮中断运行验收。已有安装包未重打包；当前已打开的是源码 Release IDE 和上述 C8T6 测试工程。

# CH32V307 调试适配与验收（2026-09-22）

CH32V307 已接入 MCU StudioX 的 OpenOCD / GDB 实机调试。VCT6、RCT6、WCU6 三种工程完成离线验证，**CH32V307VCT6 + WCH-LinkE 完成实际 IDE 操作验收**。现有 `wch.ch32v307/0.1.1` 器件包无需修改；需要本次源码构建的 IDE，已有 0.2.0 安装包未重新打包。

## 目标与实现

| 项目 | 当前范围 |
| --- | --- |
| 型号 | CH32V307VCT6 / CH32V307RCT6 / CH32V307WCU6，逐项匹配 |
| 内存布局 | Flash 映射起点 `0x00000000`、256 KiB；RAM `0x20000000`、64 KiB |
| 开发环境组件 | `wch.riscv/1.0.0`，编译器 `wch-gcc-12.2.0-v1.4` |
| 调试器 | 厂商 OpenOCD 0.11 与 `riscv-wch-elf-gdb.exe` |
| 探针 | 单台 WCH-Link / WCH-LinkE，RISC-V / SDI，序列号留空 |
| 可选速度 | 400 / 4000 / 6000 kHz；本次实板使用 6000 kHz |

- 新增 `WchDebugTarget`，校验型号、架构、开发环境组件版本、存储布局和器件脚本。旧 0.1.0 编译专用工程、未收录型号、错误布局或其他探针不会进入调试会话。
- 复用 0.1.1 包的 WCH-Link 接口与芯片识别脚本，使用旧版 OpenOCD 的 `gdb_port` / `gdb_flash_program` 命令，以及实际 target `wch_riscv.cpu.0`。
- GDB 明确选择 `riscv:rv32`，设置 `mem inaccessible-by-default off`。该设置与 [WCH 官方 MounRiver 工程调试配置](https://github.com/openwch/ch32v307/blob/main/C%2B%2B/Use%20MRS%20Create%20C%2B%2B%20project-example/CH32V307VCT6/CH32V307VCT6.launch)一致；会话仍使用 StudioX 的有限超时。
- 厂商 GDB 枚举了 440 个通用 RISC-V 寄存器名称，包括未实现的向量/虚拟化项目。界面只请求整数、浮点和基础机器 CSR，本次实际读取 74 项；编号从当次 GDB 名称表获取，不硬编码编号。
- 新 CH32 工程默认观察 `$pc`，保留已保存的用户观察项。可添加 `SystemCoreClock` 等固件符号。
- 延用已有的会话状态机、MI 解析、调用栈、源码定位、只读变量/内存、硬件断点与探针互斥机制。

## 附加与退出

「调试」先构建并保存独立 ELF 快照，再暂停目标、核对芯片身份/保护状态/内存划分，并比较板上装载映像。它不自动下载固件；板上程序与 ELF 不一致时退出并提示先下载。

OpenOCD RAM 工作区设为 0，校验采用主机读回比较，避免覆盖目标全局变量。GDB Flash 编程关闭；CH32 会话不执行 Cortex-M 指令。服务仅监听本机回环地址，调试与下载共用探针锁。

正常结束时删除调试器断点、GDB detach、退出 GDB；OpenOCD 执行 `resume` / `poll`，确认状态为 running 后输出 `STUDIOX_DETACHED_RUNNING`，随后释放进程和探针。暂停状态与运行状态下的结束均实际验证。

## 实板与 IDE 验收

环境：Windows，CH32V307VCT6，WCH-LinkE 固件 2.16，SDI 6000 kHz。使用此前已下载的最小 SPL 固件，工程位于 `.artifacts/CH32V307-Download-Test`。本轮没有重新下载、擦除 Flash 或修改选项字节。

以下操作通过实际构建的 WPF IDE 执行，日志保留 GDB MI 和 OpenOCD 原始输出：

| 操作 | 实际结果 |
| --- | --- |
| 附加与校验 | 核对芯片、256/64 划分及固件后，停在 `main.c:7`，PC `0x226` |
| 源码硬件断点 | 在 `main.c:5` 点击断点槽，GDB 返回硬件断点，地址 `0x224` |
| 复位与继续 | 复位停在 `_start`，继续后命中 `main.c:5` 的硬件断点 |
| 进入函数 | `main` → `System_Init`，源码定位和调用栈同步更新 |
| 逐过程 | `System_Init` 第 6 行执行到第 7 行 |
| 再次进入 | 进入 `SystemCoreClockUpdate`，显示三层调用栈 |
| 局部变量 | 读取 `tmp`、`pllmull`、`pllsource` 等变量及类型 |
| 跳出函数 | 返回 `System_Init`，GDB 报告 `function-finished` |
| 内存 | 从 `0x20000000` 读取 64 字节 SRAM；时钟全局变量位于 `0x20000010` |
| 运行、暂停 | 从主循环继续后暂停，收到 SIGINT，并定位 `main.c:7` |
| 运行时结束 | 自动中断、清除断点、detach，收到目标恢复运行标记，界面显示探针已释放 |
| 不断电再连接 | 退出后以最终构建重新打开 IDE 并附加；`$pc = 0x226 <main+6>`，`SystemCoreClock = 144000000` |
| 暂停时结束 | 最终会话正常结束，收到恢复运行标记，无残留 OpenOCD / WCH GDB 进程 |

核心日志（均为仓库本地生成物，不纳入 Git）：

- 首轮应用服务附加：`.artifacts/CH32V307-Download-Test/.build/download-6b5b3b0ce33c47a7b348b157c31112bb/debug-session.log`。
- 完整 IDE 操作：`.artifacts/CH32V307-Download-Test/.build/download-c38a891ee1db4349b630c819940119c3/debug-session.log`；18:22:03 确认运行后分离。
- 最终版本再次连接：`.artifacts/CH32V307-Download-Test/.build/download-f4fb759b38d84f70bc3f883e3463e6a0/debug-session.log`；18:28:57 确认运行后分离。

## Flash 回读与构建回归

完整 IDE 单步/断点操作后，独立 OpenOCD 会话回读物理地址 `0x08000000` 起 288 KiB，与此前下载验收基线逐文件摘要比较；两份文件均为 294912 字节，SHA-256 相同：

```text
5EE59CBF40BED95CC52CF2929655BE1173F7692C6DB6B3A049CBF3AAF6AEE06D
```

比较结果：`.artifacts/ch32-debug-flash-comparison.json`。回读日志：`.artifacts/ch32-debug-readback.log`；会话完成后确认目标恢复运行。该记录验证 Flash 内容未变化；本轮没有另行记录选项字节逐字节比较结果。

| 检查 | 结果与记录 |
| --- | --- |
| Release 全量构建 | 0 警告 / 0 错误；`.artifacts/ch32-debug-final-build.log` |
| CH32 三种封装 | 真实编译、厂商 GDB 符号、OpenOCD noinit、无下载附加配置、错误配置拒绝、默认观察项、稀疏寄存器编号过滤全部通过；`.artifacts/ch32-debug-offline-final/result.txt` |
| 现有 F407 调试回归 | MI / 状态机、特殊断点规则、错误恢复、绑定回滚、运行到光标与暂停竞争等通过；`.artifacts/ch32-debug-common-regression/result.txt` |
| AG32 离线回归 | 两种模板编译、符号、会话配置及 RISC-V 寄存器映射通过；`.artifacts/ch32-debug-ag32-regression.log` |

离线复现命令（输出目录必须不存在）：

```powershell
dotnet run --project tools/StudioX.DebugValidation -c Release -- --wch artifacts/packs/CH32V307-0.1.1/wch.ch32v307-0.1.1.mcupack artifacts/tool-runtime .artifacts/ch32-debug-check-new
./tools/Build.ps1 -Configuration Release
```

`--wch` 使用真实厂商工具解析配置和 ELF，但 `noinit` 阶段即关闭 OpenOCD，不初始化 USB、不连接硬件。

## 尚未验收的范围

- RCT6 / WCU6、非 E 型 WCH-Link，以及 400 / 4000 kHz 的实板操作。
- CH32 的条件、计数、临时、日志断点与运行到光标的实板行为。共用逻辑通过离线回归，不把它视为 CH32 硬件验收。
- 硬件断点资源耗尽、复杂优化代码、中断密集场景、FPU 算术执行、多线程 RTOS、异常 USB 拔插及掉电恢复。
- 寄存器写入、外设 SVD、反汇编和 RTOS 线程识别尚未提供。

本次输出程序为 `src/StudioX.Desktop/bin/Release/net10.0-windows/MCU StudioX.exe`。器件包和已有安装包均未改版。

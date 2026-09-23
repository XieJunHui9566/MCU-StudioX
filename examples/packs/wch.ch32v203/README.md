# CH32V203 · 标准库

使用本机 MounRiver Studio 2 的官方 SDK 制作，来源版本和哈希见 `vendor/provenance.json`。
收录型号、芯片 ID 与内存边界见 `vendor/devices.json`。不同容量不能互换下载配置。

- C6/F6/G6：32 KiB Flash / 10 KiB SRAM；C8/F8/G8/K8：64 KiB Flash / 20 KiB SRAM。
- RBT6：官方 D8 启动代码，160 KiB Flash / 32 KiB SRAM；下载和调试拒绝其他选项字内存划分，不修改选项字节。
- CCT6：官方 CH32V205 分支的独立标准库和启动文件，256 KiB Flash / 32 KiB SRAM，不能使用旧 V20x 外设定义。
- 默认使用内部 HSI 8 MHz，经 PLL 运行：V20x 为 144 MHz，CCT6 为 160 MHz，不依赖板上外部晶振。USB 等精确时钟应用应按实际硬件修改 `device/system/` 中的时钟配置。
- `main` 保持精简；`System_Init()` 配置优先级分组、更新系统时钟和延时基准。默认不启用 GPIO、串口、USB 或定时器。
- 中断入口使用官方启动配置和 `__attribute__((interrupt("WCH-Interrupt-fast")))`；C++ 中断入口还需 `extern "C"`。不要把 V307 的中断控制寄存器值直接移植到 V203。
- WCH-Link / WCH-LinkE 使用 RISC-V / SDI，速度为 400、4000 或 6000 kHz；当前支持单台探针，序列号留空。调试核对板上固件，不隐式下载。

WCH 原始版权和仅用于沁恒芯片的使用声明保留在源文件中。编译通过不代表所有封装均完成实板验证。

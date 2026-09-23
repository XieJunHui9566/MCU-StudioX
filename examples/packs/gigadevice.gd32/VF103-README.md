# GD32VF103 · RISC-V

收录 GD32VF103 的 14 个常见正式料号：QFN36、LQFP48、LQFP64、LQFP100 的 16/32/64/128 KiB 容量变体，以厂商 [GD32VF103 Datasheet Rev2.3](https://www.gd32mcu.com/data/documents/datasheet/GD32VF103_Datasheet_Rev2.3.pdf) 型号表为准。工程使用官方 V1.7.0 标准外设库与 RISC-V/ECLIC 启动代码，SDK 固定提交及 SHA-256 见 `provenance.json`。

- 工具集为 StudioX 内置 `riscv.xpack/1.0.0`，编译参数 `rv32imac_zicsr_zifencei / ilp32`（GCC 15 将 CSR 和栅栏指令扩展显式列出）；每个容量使用厂商对应的 `.lds` 内存布局。
- 厂商默认系统配置使用内部 IRC8M 经 PLL 至 48 MHz，模板不预设外部晶振或板上引脚。厂商头文件仍要求名义 `HXTAL_VALUE`，模板定义为 8 MHz；当前内部时钟分支不会启用 HXTAL。
- 默认只编译启动、系统、N200 核心支持及 RCU/GPIO；其他原厂标准外设驱动保留在 `device/sdk/peripheral/`，按需要加入工程构建。
- 当前包不开放下载/调试。虽然内置 OpenOCD 有 `gd32vf103.cfg`，尚未在无样品条件下验证逐型号身份和容量校验。编译成功仅代表软件侧验证。

保留厂商与 Nuclei 原始许可，源码仅用于 GigaDevice 器件。

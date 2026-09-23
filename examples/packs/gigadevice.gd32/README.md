# GD32 软件适配包

这些 StudioX 格式 1 器件包按 GD32 厂商固件系列分开生成。型号和 Flash / 主 SRAM 容量来自 GigaDevice 的 Keil DFP；CMSIS、标准外设库和启动文件来自兆易创新发布的固件库固定提交或官方版本。具体来源、哈希与许可证位于每个包的 `provenance.json`、`vendor/source.pdsc` 和 `licenses/`。

- 默认模板使用厂商 `SystemInit` 提供的内部时钟分支，不假设开发板有外部晶振、LED、串口或其他引脚连接。
- 模板构建厂商 RCU、GPIO 驱动，以及存在时的 `misc.c` 中断辅助模块。其他标准外设库头文件、源码在工程的 `device/sdk/peripheral/`，按实际型号与需要加入构建；这些其他驱动尚未逐项编译验证。用户业务代码放在 `src/`。
- F10x/F20x 最新官方固件库没有 GNU 启动文件；构建脚本逐项提取原厂 ARMASM 向量表，生成 GNU 启动代码，并在 `vendor/vectors.json` 保留向量顺序及原文件校验值。
- F4xx DFP 中的 F403 使用单独发布的 F403 固件库，因此是独立包。F4xx 的 0x10000000 辅助 SRAM 默认不链接，工程仅使用 DFP 给出的主 SRAM。
- E50x 使用官方 1.7.0 固件库与 1.9.0 DFP；原 SDK 漏带 GCC 所需的 `cmsis_gcc.h` 和 `mpu_armv8.h`，包内补入 SHA-256 锁定的 Arm CMSIS_5 5.2.0 原始头文件。EPRT 是专用系列，DFP 中两个缩写与当前正式料号未能完整对应，故不收录于 E50x 包。
- 当前包不开放 OpenOCD 下载或调试入口：没有实板和可靠的型号身份校验，不能将编译成功视为安全烧录依据。后续取得具体样品后，再逐型号补充 SWD 探针、芯片 ID、容量校验与实板验证。
- [GD32E23x User Manual Rev1.5](https://www.gd32mcu.com/data/documents/userManual/GD32E23x_User_Manual_Rev1.5.pdf) 给出 Flash density 字段及 DBG_ID 寄存器地址，但未给出可逐型号验证的 ID_CODE 常量；因此 E23x 也暂不开放下载。

本版 Arm 器件包覆盖 C10x、E10x、E23x、E50x、F10x、F1x0、F20x、F30x、F3x0、F403、F4xx、L23x，共 384 个 DFP 型号；VF103 的 14 个正式料号见独立 RISC-V 包。E51x、F50x、W51x 等尚缺完整的器件包适配与离线真编验证，未纳入可编译支持。

维护者运行 `tools/New-Gd32Packs.py`，使用 `sources.json` 锁定的官方源码 ZIP、E50x 官方 7z 与 Keil GigaDevice DFP `.pdsc`；E50x 的官方 7z 解包需安装 7-Zip。该脚本不联网、不连接硬件。授权条件见各包 `licenses/` 内原始文件。

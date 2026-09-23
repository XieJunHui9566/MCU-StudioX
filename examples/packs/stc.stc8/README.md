# STC 8 位单片机单包（STC15 首批型号）

本目录是 `stc.stc8` 的构建配方，不包含从 AiCube 原样复制的厂商头文件。当前单个 StudioX Pack 收录官方资料能逐一核实容量的 STC89、STC12、STC15、STC8G、STC8H 共 24 个完整型号。其他 STC 8 位型号可继续添加到同一个包，但需要逐型号核对程序区、idata、xdata 和寄存器适用性。STC16、STC32 和 C251 系列不在本包范围内。

各系列使用 AiCube 中对应的 SDCC 寄存器表。`tools/New-Stc8Pack.py` 从用户指定的本地 AiCube 解包目录读取这些表，核对 SHA-256，只提取 SFR 名称和地址等器件事实，再以 StudioX 独立格式生成头文件；原注释、示例和原始文件不打入包。AiCube 解包内容没有发现明确的再分发许可，因此不能把原文件当作已获授权的 SDK。生成包内的 `vendor/provenance.json` 记录每份来源哈希和许可状态。

## 构建

```powershell
python tools/New-Stc8Pack.py --aicube-root "<AiCube 解包目录>" --output artifacts/packs/STC8-0.1.0
```

`--aicube-root` 指向包含 `AiCube_sources_and_firmware_extracted` 的目录。脚本不下载 SDK、不会调用烧录器；输出为一个 `stc.stc8-0.1.0.mcupack`。

工具集是 SDCC 4.5.0 `mcs51`，由 IDE 通过 CMake + Ninja 调用。包不包含 SDCC、CMake、Ninja 或烧录程序，也没有 OpenOCD 配置。编译优化等级在 IDE 工程设置里选择。

## 容量及代码边界

STC 官方 [STC15 系列数据手册](https://www.stcmicro.com/datasheet/STC15F2K60S2-en.pdf) 的器件表和存储器章节给出这批型号的 8–61 KiB 程序 Flash、256 字节 idata、1792 字节片内 xdata。手册还说明程序区最后 7 字节固定保存 Global ID，因此 STC15 的 SDCC `--code-size` 按器件表容量减 7 字节设置。代表型号 IAP15F2K61S2 是 61 KiB Flash（0x0000–0xF3FF）和总计 2 KiB SRAM。它作为普通应用编译时无需预留仿真监控固件；本包不提供调试。

其他四个系列的逐型号容量依据 STC 官网：[STC89C51RC 系列](https://www.stcmicro.com/stc/stc89c51rc.html)、[STC12C5A60S2 系列](https://www.stcmicro.com/stc/stc12c5a32s2.html)、[STC8G1K08 系列](https://www.stcmicro.com/stc/stc8g1k08.html)、[STC8H1K08 系列](https://www.stcmicro.com/stc/stc8h1k08.html)。片内 RAM 拆分为 STC89C52RC 的 256 idata + 256 xdata，其余新增型号为 256 idata + 1024 xdata。STC8G/STC8H 系列通用头列有其他派生芯片寄存器，实际外设须以所选实物手册核对。

STC15F 系列为 5 V 电压档，STC15L 系列为 3 V 电压档；工程模板不预设板级 GPIO、晶振或串口。实际焊盘、供电和时钟设置以实物与官方资料为准。

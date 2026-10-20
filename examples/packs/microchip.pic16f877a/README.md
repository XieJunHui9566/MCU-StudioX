# PIC16F877A 初步支持

依据 Microchip PIC16F87XA 数据手册 DS39582B，以及 PIC16Fxxx DFP 1.9.176 中的准确器件定义。
器件具有 8192 个 14 位程序字、368 B 分银行 RAM、256 B EEPROM。格式 1 的 `flashBytes` 表示物理位容量折算的 14336 B；`ramOrigin=0` 为容量描述占位，不表示 368 B 连续 RAM。地址与分银行信息由 `pic` 适配处理，不交给通用下载/内存区间逻辑。

使用独立 XC8 4.00 与官方 DFP 编译，不需要 MPLAB X IDE。第一阶段只支持 C99、O0、`src/` 下的 C 源码和 `include/` 头文件；不使用 GCC、CMake、OpenOCD 或 Arduino。
基础模板不设置板级配置位；GPIO 示例明确使用外部 4 MHz 与 RD0，不能代表未知开发板接线。尚未接入 PICkit 烧录或实机调试。

来源：
- https://ww1.microchip.com/downloads/en/devicedoc/39582b.pdf
- https://packs.download.microchip.com/Microchip.PIC16Fxxx_DFP.1.9.176.atpack
- https://www.microchip.com/en-us/tools-resources/develop/mplab-xc-compilers/xc8

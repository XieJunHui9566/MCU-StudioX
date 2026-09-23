# GD32E51x 离线器件包

本包由官方 GD32E51x 固件库固定提交、官方 AddOn 1.5.0 中的 DFP 与 Arm CMSIS 5.2.0 缺失头文件生成。`sources.json` 锁定下载地址和 SHA-256，包中保留原许可证、`vendor/source.pdsc`、向量表来源及哈希。

收录 DFP 的 20 个常规基础型号：E513 8 个、E515 2 个、E517 6 个、E518 4 个。DFP 中另有两个 `GD32EPRT*` 专用料号；它们不属于这些通用型号，未收录。DFP 的 `GD32E513ZE` SRAM 错列为 96 KiB；官方 GD32E513xx Datasheet Rev1.6 表 2-1 给出 128 KiB，包按该数据手册修正。型号名为官方 DFP 基础型号，订货尾缀（封装温度、包装）不擅自扩展为已验证的精确料号。

每型号提供 CMSIS 最小工程和官方标准外设库最小工程。启动向量顺序来自原厂 ARMASM，GNU 启动文件保留原向量顺序。默认使用原厂 `SystemInit` 的内部 IRC8M 分支，不假设外部晶振或板级引脚。仅 RCU、GPIO、misc 进入默认构建；其余外设源码保存在工程目录，用户需要时再加入。当前没有实板，不配置 OpenOCD、下载或调试。

资料：[官方固件库](https://github.com/GigaDevice-GD32-MCU/GD32E51x_Firmware_Library)、[官方 AddOn 1.5.0](https://www.gd32mcu.com/download/down/document_id/605/path_type/1)、[E513 数据手册 Rev1.6](https://www.gd32mcu.com/data/documents/datasheet/GD32E513xx_Datasheet_Rev1.6.pdf)、[E517 数据手册 Rev1.4](https://www.gd32mcu.com/data/documents/datasheet/GD32E517xx_Datasheet_Rev1.4.pdf)、[E518 数据手册 Rev1.3](https://www.gd32mcu.com/data/documents/datasheet/GD32E518xx_Datasheet_Rev1.3.pdf)、[E515 在官方 AN262 中的说明](https://www.gd32mcu.com/data/documents/applicationNote/AN262_Migration%20from%20GD32E50x%20series%20to%20GD32E51x%20series.pdf)。

复现生成：先将 `sources.json` 中四个文件下载到同一目录，随后运行 `python tools/New-Gd32E51xPack.py --sources <目录> --output <新目录>`。需要 7-Zip 和已构建的 StudioX CLI。脚本只处理本地文件，不访问硬件。

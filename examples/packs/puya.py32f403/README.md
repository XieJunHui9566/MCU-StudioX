# Puya PY32F403 软件器件包

此 StudioX 格式 1 包覆盖普冉官网 PY32F403 数据手册 V1.9 表 1-1 的 11 个常规完整料号，提供 CMSIS、HAL 和 LL 三种 C 工程模板。各型号的 Flash/SRAM 容量、封装来自[普冉官网产品页](https://www.puyasemi.com/py32f403xilie589.html?tag=15)，详细核对链接见 `catalog.json`。默认工程仅用内部 HSI 时钟，不假设板上有外部晶振、LED 或指定引脚。

包内启动文件、系统文件、CMSIS 头和 HAL/LL 库来自 [OpenPuya/PY32F4xx_Firmware](https://github.com/OpenPuya/PY32F4xx_Firmware) 的固定提交。**OpenPuya 是非官方资料镜像**，仓库自述并非普冉官方组织；这些源码文件保留原厂及 ARM/CMSIS 的许可声明。`provenance.json` 记录提交、文件校验值、原始 DFP 及与现行官网容量冲突之处。普冉官网现列 Firmware Library 1.4.8 和 Keil DFP 1.0.12；本包所用镜像/DFP 较旧，未冒充现行原厂发行版。

目前仅开放创建、编辑和 ARM GNU 编译。没有实板与严格芯片身份核验，包内不声明 OpenOCD，因此 IDE 不开放该系列的下载和调试入口。2026 年新列的 `-C` 修订料号有单独数据手册/参考手册，当前固定源码无法确认寄存器和启动文件兼容性，暂未纳入。

维护者可用 `tools/New-Py32F403Pack.py --source <固定提交的 OpenPuya 仓库> --output <全新目录>` 重建。脚本离线运行，不访问芯片。

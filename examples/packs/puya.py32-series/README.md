# 普冉 PY32 系列器件包

StudioX Pack 格式 1，每个子系列独立 `.mcupack`。设备名采用普冉 DFP 的容量通配写法：例如 `PY32F030x8` 适用于对应 64 KiB Flash / 8 KiB SRAM 的 F030 封装变体。`x` 不是固定长度的订货号字符；创建工程前应以实物丝印、数据手册确认系列和容量。模板默认使用片内时钟，不占用板级 GPIO。

每个器件提供 CMSIS 寄存器、厂商 HAL、厂商 LL 三种空白模板。HAL/LL 的源码和头文件按源库复制，原始版权及许可证保留在包内；可编辑配置头位于 `device/sdk/config/`。目前未提供面向 PY32 的安全芯片身份检查，因此这些包不声明 OpenOCD 下载/调试配置；无样品，不宣称实板验证。

维护者使用 `tools/New-PuyaPacks.py --source-root <OpenPuya 镜像目录> --output <输出目录>` 重建。生成器锁定社区镜像的 Git commit、提取镜像中 Puya DFP 的型号/内存表，并与普冉官网资料交叉核对；构建时不联网。详见输出包中的 `provenance.json` 与仓库 `docs/PY32.md`。

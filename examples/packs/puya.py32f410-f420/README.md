# PY32F410 / PY32F420 离线器件包

- F410 与 F420 独立打包。F410 提供 CMSIS、HAL、LL；F420 原厂库无 LL 源码，提供 CMSIS、HAL。
- 完整料号、容量按普冉现行数据手册和产品页核对。F410G18U7 的官网产品页与数据手册对 SRAM 容量有冲突，暂不收录。F420 仅收录官网已确认的 R1CT7、C2CT7。
- 使用固定提交的 [OpenPuya](https://github.com/OpenPuya) 社区镜像；这是非官方资料镜像。包内保留源文件 SHA-256、原许可证及 DFP 来源。
- 当前只支持工程生成与 GNU Arm 离线编译。无样片验收，未提供下载、调试或 OpenOCD 配置。

正式量产前请从[普冉官网](https://www.puyasemi.com/download.html)复核最新资料并在实板上验证时钟、引脚、下载与调试。

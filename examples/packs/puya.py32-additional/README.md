# PY32F031/F032/F033/F090/F092 补充器件包

每个子系列独立打包，提供 CMSIS 寄存器、Puya HAL 和 Puya LL 最小空白工程。模板只使用内部默认时钟，不配置开发板 GPIO。HAL/LL 库源码、配置头和许可证保留在包内。

型号与内存取自镜像所附 Puya DFP，并以普冉官网产品/数据手册交叉核对。F031 DFP 包含 x4/x6/x7/x8 四个容量分支；当前官网产品页能确认的 F031 成品是 x8（64K Flash/8K SRAM），所以其余三个分支只在 `catalog.json` 记为未收录。通配 `x` 不代表固定长度的一位订货号字符。

F031/F032 使用固件库原有 GCC 启动文件和链接布局。F033/F090/F092 镜像仅附 Keil/IAR 启动工程；StudioX 按 Puya DFP 的 ARMASM 中断向量生成 GCC C 启动文件，并使用同厂 F032 GCC 链接段布局，按各系列 DFP 改写 Flash/SRAM 边界。包内 `provenance.json` 记录镜像 commit、DFP SHA-256、生成依据与 SDK 文件哈希。OpenPuya 是非官方社区镜像，固件版权及许可证归原作者。

当前没有样品。仅做离线工程生成、真实编译和 ELF/内存检查；不声明 OpenOCD 下载/调试配置，不代表实板验证。

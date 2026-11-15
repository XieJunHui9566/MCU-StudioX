MX251 固定开发验证工程：STC32G12K128 原生 USB + 整数 + 中断

当前版本、工具身份和 small/large 统一验收入口见 ../README.txt、../acceptance.json。
下列 V0.0103 说明为硬件证据来源；当前矩阵两个整数固件仍与这些原始 HEX 相同。

这是内部验收源码，不是 IDE 器件包或发行版。V0.0103 修改编译器向量生成，
目标 profile 预留 62 个中断槽；专用启动代码统一处理中断交接。
small/large 使用同一套完整验收源码，数学功能和运行库未缩减。

初始化约束
本工程只使用 Timer0 中断，USB 采用轮询。目标专用 startup.asm 在栈与 C 初始化
之前写 IE=0、IE2=0，main 配置 Timer0 后开启 ET0/EA，不再逐工程补 IE2=0。
不得假定 USB bootloader 跳转到应用时所有外设都已复位。
STC32G 手册第 642 页指出 EUSB 为只写位，读回始终为 0；第 1698 页进一步
说明 IE2 不能以读-修改-写方式保留 EUSB。写入本工程拥有的完整初值才可确定关闭。
此初始化不能直接移入会使用 SPI、UART2/3/4、Timer2/3/4 中断的通用 USB 库；
那些应用应由统一的中断配置所有者维护 IE2 软件副本并写入完整值。

验证范围
- 当前 STC32G12K128 样品，F7E3 / BSL 7.4.5U，BSL 报告 IRC 11,059,200 Hz。
- CMake + Ninja 管理构建；mx251.json 只描述目标、模型和运行库配置。
- small 和 large / stack-auto / 普通 ABI 调用 + ABI4 MDU32 运行库。
- 640 输入、每组 10 项运算，涵盖有符号/无符号乘除余数、边界与直接运行库约定。
- Timer0 中断内另执行 5 项运算并校验；前台通过 HID 回传原始结果。
- small/large 各 6400/6400 结果正确；采集窗口中断增量均为 3284，错误均为 0。
  两个模型使用相同输入域，不能宣称 12800 个不同用例或据中断数计算速度。
- CODE 窗口 FF0000..FFFFFF；EDATA 4 KiB，栈 0400..07FF，XDATA 8 KiB。
- 中断 0..61 的完整槽位、跳转和默认停机指令已经离线逐字节核对；实板正常回归通过。
  未在实板主动注入未知中断。main 所在翻译单元须可见所有 ISR 声明。
- 全部 128 KiB Flash、任意 ISR/ABI、浮点与其它外设仍待单独验证。

复现
当前统一入口使用 build/qualified、V0.0111 profile-final 和 V0.0110 clean-tools。
profile 锁定当前编译器覆盖文件，以及目标 startup 和 62 个中断槽策略。
只有编译器来自覆盖文件，预处理器/汇编器/链接器/头文件和基础库仍使用锁定父工具链。
不能用旧 profile 编译新版源码，也不能以正式 IDE 的 AI8051U profile 冒充 STC32G。
将本目录源码复制到新的验证目录，用 CMake 的 MX251_PROJECT_MODULE、
MX251_BUILD_SCRIPT、MX251_PROFILE、MX251_TOOLCHAIN、MX251_PWSH 以及 Ninja
路径参数显式指定依赖。可执行复现入口和完整命令记录见本次证据目录
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0103。
源码工程不绑定开发者的绝对工具路径，不修改全局 PATH，也不自动连接硬件。

原始来源与证据
数值向量沿用 round28/integer-oracle.json，由独立 Python 整数参考核对。
USB 数值寄存器定义核对 STC 官方头文件；轮询协议实现沿用已封存实验源码。
相关来源、原始官方资料和许可信息保持在 mx251-v0.0100 与
stc32g-native-usb-20261027；未复制或运行官方示例的 DFU/IAP 擦写实现。
官方手册：https://www.stcaimcu.com/data/download/Datasheet/STC32G.pdf
V0.0101/V0.0102 原证据保持封存；本次向量修改、双模型 HID、ACK、独立审计和哈希保留。
未做 Flash 回读。未替换安装版工具链，不标注 LST。

MX251 V0.0107：使用新的中断链接契约编译器重建，small/large HEX与V0.0106逐字节相同。本轮未重新下载，不新增硬件资格。

V0.0108：修复兼容ISR重复声明的实体保留；同输入重建的完整固件与V0.0107逐字节相同，没有新的硬件采集。

MX251 固定开发验证：STC32G12K128 完整工程的软件数学路径

当前版本、固定配置、工具身份和一条命令验收以 ../README.txt、../acceptance.json 为准。
下列 V0.0106/V0.0108 说明为历史语义与证据；V0.0110 刷新运行库后的新固件基线已列入当前矩阵。

这是内部验收工程，不是 IDE 器件包、示例发行包或 LST 版本。
V0.0108 当时补齐跨文件中断链接契约，固件与 V0.0106 逐字节相同；后续库刷新以当前矩阵为准。

验证范围
- 沿用原固定数学工程的全部 123 个 soft=true 输入，不另造有利输入。
- 整数乘/除/余数、浮点加减乘除、sqrt、sin/cos/tan/atan、整数浮点转换。
- native-math 与 native-math-mdu32 均在当前 STC32G12K128 样品上通过 123/123。
- 每项 3 个采样，每采样 8 次间接调用；Timer0 /12、预分频 0、EA=0。
  首次计算后缓存原始结果，USB 重播不会被算作新的测试或采样。
- 误差要求沿用 abs(error) <= 1e-6 + 2e-6*abs(reference)，不是逐位 IEEE754 认证。
- 原工程的 22 个硬件专用输入不在该软件域内；STC32G 的 TFPU 资格尚未确认。
- frexpf/ldexpf、完整特殊值与三角函数全域边界语料未在本工程内覆盖。
- 本数学运行库 profile 仅接受 small / stack-auto；不能绕过 large 守卫。
- 本工程计时期间关闭中断。并发整数与 Timer0 ISR 的证据在相邻 stc32g-native-usb 工程。
- 当前仅验证 64 KiB CODE 窗口、4 KiB EDATA、8 KiB XDATA；本实验主机限制镜像跨度 32 KiB。

构建
当前统一入口使用 build/qualified、V0.0111 profile-final 和 V0.0110 clean-tools。
手工构建须配置 CMake + Ninja，显式传入 MX251_PROJECT_MODULE、MX251_BUILD_SCRIPT、
MX251_PROFILE、MX251_TOOLCHAIN、MX251_PWSH 与 CMAKE_MAKE_PROGRAM。
所有源码、头文件和宏都由 CMake 管理；mx251.json 只保存目标、ABI、模型和运行库设置。
默认 backend=ordinary-wide、explicitAbi=false、runtime=native-math-mdu32；改为 native-math 后重新配置即可构建纯软件对照。
CMake 根据 runtime 设置回传标识，不能脱离固件实际配置伪造测试变体。
工具路径由调用者锁定，不写入源码，不修改 PATH，不自动连接或下载硬件。

比较边界
C251 对照使用同一组输入与数学表达式，SMALL / ROM(HUGE) / OPTIMIZE(7,SPEED)，
数学内核和间接函数指针声明 reentrant。C251 的传输层和 main 使用普通 SMALL ABI。
本机没有定位到可核验的厂商 C251 MDU32 替换库，不能把 MX MDU32 对 C251 软件库
称作相同硬件加速条件；公平软件比较与 MX 自身 MDU32 前后比较分别记录。
C251 特定循环右移拆字节的生成错误用计时区外的显式常量移位避开；数学内核未改。
源代码、原始失败、指令核对、独立 Python 数学参考、运行回传与完整比较保留在
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0106；历史 C251 原始记录在 mx251-v0.0104。

来源与许可证
原始向量和数学表达式来自本项目已封存的 math-comparison 与 mx251-v0.0100 工程。
USB 轮询实现沿用 stc32g-native-usb 验收，寄存器来源为 STC 官方手册及头文件；
官方资料和许可记录保留在 mx251-v0.0100、stc32g-native-usb-20261027。
未复制 Keil 库实现或二进制到仓库，C251 验证要求本机合法安装的独立工具链。
下载 ACK、运行回传分别审计；没有 Flash 回读，未动 AI8051U 或已安装 IDE 工具链。

V0.0106 计量注意
使用同一 measure 函数的原始 ticks 中位数 / 8 次调用，保留循环及间接调用成本。
原样返回已恢复为单条 ERET，实测从 V0.0105 的4.625回到3.75 tick/call。
不扣除各固件自己的 identity，不把其修复或未改数学函数的小幅抖动算成数学优化。
软件/MDU 两种结果在原有123输入上与V0.0105逐位一致；完整边界域仍未因此补齐。
只有三个直通函数生成代码改变，总体积各减少66字节；跨文件ISR诊断为下一交付缺口。

V0.0107 复核
本轮重建及源码输入核对通过；没有新的板卡采集或性能成绩，沿用V0.0106原始证据。
跨文件中断须在main文件包含同名同槽原型；缺失/冲突由编译器生成的对象契约与链接器检查。

V0.0108：修复兼容ISR重复声明的实体保留；同输入重建的完整固件与V0.0107逐字节相同，没有新的硬件采集。

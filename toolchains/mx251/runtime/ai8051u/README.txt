MX251 V0.0100 — AI8051U MDU32 开发运行库

本库是目标相关的可选运行库，使用既有 MX251 ABI4，不改变编译器核心二进制。
输入 DR4/DR12，返回 DR4，临时使用 DR0、A 与标志位；使用当前寄存器组，
不占静态暂存、不切换寄存器组、不屏蔽中断。DMAIR 必须用 MOV direct,#immediate
写入。原软件整数运行库保留，未自动替换安装版 IDE 的默认运行库。

自动编译入口仍为 CMake + Ninja + MX251，适用配置由 Build.ps1 和 profile.lock.json
共同约束。native-int-mdu32 用于 small/large stack-auto；native-math-mdu32
用于已验收的普通 ABI 完整数学工程，native-math-wide-mdu32 用于 small 显式 ABI4
数学基准。其它目标不能仅凭 251 指令兼容选择此库；目标、ABI、触发方式、库哈希
必须匹配。没有声称任意手写 ISR、任意 ABI 或全部 C 特性均已验收。

直接运行库调用维持原有有界行为：无符号除零返回 FFFFFFFF，余数保留被除数；
有符号除零保留原软件库约定，INT_MIN/-1 返回模 2^32 的商与零余数。
C 运算符的除零及 INT_MIN/-1 仍是未定义行为，测试只在直接调用层检查这些约定。

独立 Python 整数参考、640 输入 × 10 结果、普通 ABI 转 ABI4、定时器中断并发，
small/large 各 6400 项通过。完整数学固件 267/268，唯一不匹配仍为已知硬件 NaN132；
软件路径无新增失败。7 项错误配置/库完整性诊断通过。

运行库使用 sdar -rcD 消除归档时间戳，两个独立构建目录得到同一对象与库哈希。
尚为开发版本，未标 LST、未制作或安装发行包。STC32G12K128 后续已完成
当前样品原生 USB 下载与 small/large 整数及 Timer0 中断实板验证，见 ../.. 下 validation/
stc32g-native-usb/README.txt。此资格不扩大为任意 STC32G、任意 ISR 或完整浮点支持。
STC32G 验证须使用目标专用 profile，不能直接复用 AI8051U 的内存配置。

证据：.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0100/report.html

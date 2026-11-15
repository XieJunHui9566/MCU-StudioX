MX251 ISR声明离线回归
check.py 只编译/链接，不连接硬件，不修改环境或父工具链。
参数全部显式传入：--compiler 新编译器 --parent-compiler V0.0107编译器
--toolchain 配套完整工具链目录 --startup 目标startup.asm --output 新的证据目录。
输出目录必须不存在，原始命令/诊断/ASM/REL/HEX保留；依赖已有Python，无自动安装。
覆盖small/large及vector-count开关、定义前后原型、共享头文件、混合声明链、静态/extern、
错槽/类型/属性/重复函数体、普通函数和MCS51不变性；合法重复源码与去重源码HEX必须相同。
默认stack-auto下__reentrant是冗余属性；另验证无stack-auto时的真实属性冲突。
这不是全部C语义或ISR运行安全证明。应与固定完整工程、跨文件链接契约及实板范围证据一起使用。

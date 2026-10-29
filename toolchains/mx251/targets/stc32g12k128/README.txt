STC32G12K128 启动约定 — MX251 V0.0103 开发验证

startup.asm 是该目标的 C 启动入口，须进入目标专用 profile 及其哈希锁，
通过 MX251 + CMake + Ninja 构建。它不是通用 251 或 AI8051U 启动文件的替换品。
代码保留原 SDCC 启动库许可证与链接例外。

在设置 SPX、调用 __sdcc_external_startup 和 C 全局/静态初始化之前，依次执行：
  MOV IE,#0      ; A8H，关闭 EA 以及 IE 组各中断
  MOV IE2,#0     ; AFH，整字节关闭 IE2 组，包含只写 EUSB
之后仍由链接器提供 __mcs251_stack_initial，遵循原 C 初始化/skip-init 流程。
不改时钟、Flash 选项、复位标志、看门狗或未列出的外设寄存器。

应用从“IE/IE2 均关闭”的状态开始，负责配置所需外设、清理请求标志、设置允许位，
最后开 EA。不得依赖 bootloader 交接的允许位。不要在正在运行的 USB 或串口库中
重复清零 IE2；运行期应由统一所有者维护软件允许位副本，并整字节写回 IE2。
EUSB 只写，不能靠读回值或读-修改-写保留；依据 STC32G 手册第 642、1698 页。

这不是全外设复位。V0.0103 的目标 profile 启用 --mcs251-vector-count 62，
完整预留中断 0..61；未声明的中断跳转到关闭 EA 并原地停机的入口，避免进入普通代码。
停机不保证外设输出关闭或看门狗不会复位；应用仍不能主动开启没有处理入口的中断。
main 翻译单元必须包含所有 ISR 的带 __interrupt(N) 声明；跨文件定义的处理函数
也必须经头文件声明可见。编译器尚不从链接对象自动收集中断，详见 compiler-patches。
不对其它中断控制寄存器猜测写零，不把 Timer0 验收等同于任意外设/嵌套中断资格。

同源完整整数工程 small/large 均已实板通过 6400 项运算、8 项初始化检查、Timer0
中断内运算与前台并发，错误均为 0。本次修改编译器向量生成，运行库和计算源码未变。
向量跳转及停机指令已逐字节检查；没有在实板主动注入未知中断或直接观察停机 PC。
当前限定 CODE 64 KiB 窗口、EDATA 4 KiB、XDATA 8 KiB，栈 0400..07FF。
尚未验证整个 128 KiB Flash、完整数学、任意 ISR/ABI；未制作或安装 STC32G 器件包。

参考：../.. 下 validation/stc32g-native-usb/README.txt
证据：.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0103/report.html
官方资料：https://www.stcaimcu.com/data/download/Datasheet/STC32G.pdf

# FreeRTOS 调试离线验证

从仓库根目录执行，复用本机已经准备的 ARM / WCH 工具集：

```powershell
dotnet run --project tools/StudioX.RtosValidation -- artifacts/tool-runtime artifacts/validation/rtos-offline
```

验证项目只引用 Engine，不复制桌面应用、SDK 或便携版。输出包含三个很小的 ELF、MI 只读命令记录及结构化快照。

`Fixtures/freertos-snapshot.c` 的字段来自 [FreeRTOS-Kernel V10.4.6](https://github.com/FreeRTOS/FreeRTOS-Kernel/tree/V10.4.6)：`tasks.c`、`queue.c`、`include/list.h` 和 `portable/MemMang/heap_4.c`。任务链表、对象注册表和堆链接均为独立构造的静态初始化数据，并经真实 ARM GDB / WCH GDB 的 DWARF 类型解释后读取；不是运行中的 FreeRTOS，也不代替实板验收。

GDB 使用 `--nx --nh`，仅加载本地 ELF。测试关闭 inferior 函数调用和写入，不创建 OpenOCD、probe、USB 或 socket，不发送启动、连接、烧录或调度命令。故障注入覆盖缺符号、可选字段关闭、原始内存错误、禁用队列注册表、新旧 SMP 不支持、坏任务/堆循环、无限期等待与挂起的区分、任务总数不一致、截断的栈读取、重复 MI 字段和非法对象表达式。

取消回归分别检查读取前取消和等待 MI 响应时取消：进行中的底层读取必须使用不可取消的 token，完成响应后停止下一次读取，保留同一适配器可继续读取，避免硬件进程传输因用户取消而进入故障状态。

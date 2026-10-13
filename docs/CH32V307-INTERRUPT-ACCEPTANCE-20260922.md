# CH32V307 中断实板验证（2026-09-22）

用户要求进一步测试中断。使用当前连接的 **CH32V307VCT6 + WCH-LinkE 2.16**，SDI 6000 kHz、`wch.riscv/1.0.0` 的 GCC 12.2.0 v1.4。先核对芯片身份、读保护及 256 KiB Flash / 64 KiB SRAM 划分，再运行测试。

结果：六种优化等级全部通过，正式验收共触发 **54,000 次中断**，没有检测到返回错误、嵌套顺序错误或主程序通用寄存器损坏。此次不需要修改 IDE 或器件包的中断实现。

## 方法与结果

测试向量表、代码和数据在 SRAM 中；映像大小 2,708–2,972 字节，入口 `0x200001c0`。准备阶段检查 ELF 的每个加载段地址，并记录二进制 SHA-256。执行时只用 `load_image ... 0x20000000 bin` 写 RAM，关闭 GDB Flash 编程与 OpenOCD RAM 工作区。不写 Flash、选项字节、GPIO 或外设时钟。测试运行使用复位后的时钟，没有配置 144 MHz HSE/PLL。

在每个 `-O0/-Og/-O1/-O2/-O3/-Os` 映像中运行以下测试。中断源为 PFIC 软件置挂起，使用 TIM2/3/4/5 对应向量，**不启动这些定时器外设**。

| 场景 | INTSYSCR 写入值 | 每种优化的重复次数 | 中断调用次数 |
| --- | --- | ---: | ---: |
| 单层 fast 中断 | `0x0b`，与当前模板一致 | 1,000 | 1,000 |
| 三级 fast 嵌套 | `0x0b`，优先级依次提高 | 1,000 | 3,000 |
| 关闭硬件压栈，使用普通 interrupt 属性 | `0x0a` | 1,000 | 1,000 |
| 三级 fast 加一级软件压栈 | `0x1b`，允许溢出后继续响应 | 1,000 | 4,000 |

每次触发前给通用寄存器设置哨兵，保留 ABI 的 gp/tp/sp；中断内调用故意改写 caller-saved 寄存器的辅助函数。汇编在中断链前后记录 x0–x31，固件逐项比较，并检查计数、嵌套深度和进入/退出顺序。三级顺序为 `0x12345`，四级顺序为 `0x1234567`。嵌套中的普通 C 局部变量和调用也参与执行，但没有逐层额外采集全部寄存器快照。

六个映像最终状态均为 `0x600d600d`，计数均为 `4000 / 2000 / 2000 / 1000`，错误数为 0。每轮加载前清零结果区与寄存器快照区，避免沿用上一次结果。软件中断入口的 C++ 链接名和六档优化的对象代码检查见 [中断约定](CH32V307_INTERRUPTS.md)；本次 RAM 实板固件使用 C。

## 原板内容与恢复

测试前备份 288 KiB 物理 Flash、16 字节选项区和 64 KiB SRAM。正式测试后逐字节比较 Flash/选项区，长度及内容完全一致；还将最终 Flash 与首轮试运行前的完整备份交叉核对。

| 区域 | 长度 | 测试前后 SHA-256 |
| --- | ---: | --- |
| Flash | 294,912 B | `5EE59CBF40BED95CC52CF2929655BE1173F7692C6DB6B3A049CBF3AAF6AEE06D` |
| 选项区 | 16 B | `1CFEE3A9A1C86C0B6F02A1A96A2270F2CB83415EA78414B5605987A40CEDA76C` |

结束执行 `reset halt; resume; poll`，检查 target 状态为 running，记录 `ORIGINAL_FLASH_RUNNING`。随后正常退出 OpenOCD，没有残留 OpenOCD/GDB 进程。SRAM 测试会改变运行时状态，原程序通过复位重新初始化，并非继续原暂停点。

首次试运行的中断结果也通过，但主机总超时 55 秒导致最后 Flash 回读只有 290,816 B；已读部分与备份一致。测试工具已修正为 120 秒有界超时、区分“不完整回读”和“内容变化”，增加每轮结果清零，再完整重跑并通过。原始试运行日志和不完整文件保留，不能作为恢复映像。

## 记录与复现

- 最终验收记录：`artifacts/ch32-interrupt-ram-verification/final/`，含源码快照、六个 ELF/BIN、反汇编、结果和寄存器快照、原始 `hardware.log` 及 `hardware-result.txt`。
- 首轮日志：`artifacts/ch32-interrupt-ram-verification/pilot/`。
- 原板备份：`artifacts/hardware-backups/20260922/ch32-interrupt-ram-1/`、`ch32-interrupt-ram-2/`；后者 `comparison.json` 为完整回读比较记录。
- 可维护的小型回归源码在 `tools/StudioX.WchValidation/InterruptRam/`，不包含复制的 SDK 或开发环境组件。

归档后逐文件核对 SHA-256；清理 `.artifacts/ch32-interrupt-ram-1/` 和 `ch32-interrupt-ram-2/` 被自动审批策略拒绝（仅返回 `blocked by policy`），这两个临时目录仍在，没有绕过限制删除。

准备不会连接硬件：

```powershell
dotnet run --project tools/StudioX.WchValidation -c Release -- --prepare-interrupts-ram artifacts/tool-runtime .artifacts/ch32-irq-new
```

以下命令会暂停、复位已连接的 VCT6 并执行 RAM 固件，须在当前任务获得实板测试授权后显式运行；备份目录必须不存在：

```powershell
dotnet run --project tools/StudioX.WchValidation -c Release -- --run-interrupts-ram artifacts/tool-runtime .artifacts/ch32-irq-new artifacts/hardware-backups/ch32-irq-new
```

本次不覆盖真实外设事件/清标志时序、144 MHz 时序压力、八级嵌套、FPU、RTOS 或中断内源码单步；没有扩大到 RCT6/WCU6 的实板验收。

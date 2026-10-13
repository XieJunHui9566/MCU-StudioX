# STM32 调试扩展：2026-09-22

## 范围

本次把 F407 的 ST-Link / CMSIS-DAP + OpenOCD 调试流程接入现有 F1/F4 精确器件目录。没有连接其他芯片，没有新增芯片执行模拟器，也没有改动器件包内容或版本。

| 内核 | 子系列 | 基础型号数 |
| --- | --- | ---: |
| Cortex-M3 | F100、F101、F102、F103、F105、F107 | 95 |
| Cortex-M4 / FPU | F401、F405、F407、F410、F411、F412、F413、F415、F417、F423、F427、F429、F437、F439、F446、F469、F479 | 149 |

内核依据：[ST STM32F1](https://www.st.com/en/microcontrollers-microprocessors/stm32f1-series.html)、[ST STM32F4](https://www.st.com/en/microcontrollers-microprocessors/stm32f4-series.html)。器件容量、地址及目标脚本来自原有 23 个独立 0.1.1 包，CubeMX 与包工程共用同一套精确型号匹配。HAL / SPL / HAL+FreeRTOS / SPL+FreeRTOS 均经过目标解析检查；RTOS 任务视图暂未提供。

## 实现

- 调试入口取消 F407 专用限制；不放行目录外系列、含糊容量、伪造前缀或错误的架构 / 内存布局。
- 保留各器件的 Flash 驱动、容量、分区、复位和 `studiox_check_target` 身份保护；不把 F407 目标脚本套给 F1。
- 寄存器标题随工程 / 会话内核切换；数值和可用寄存器继续按 GDB 实际返回的名称及编号读取。
- 硬件断点不再固定 6 个；容量不足由 OpenOCD 返回错误。普通、条件、计数、临时、日志断点和运行到光标共用原有 MI / 会话实现。
- 延续附加时 ELF 校验、禁止隐式下载、保护运行时 RAM，以及结束时删除断点、退出 GDB、确认目标恢复运行并释放探针的机制。
- J-Link 保持原有下载支持；本次调试探针仍为 ST-Link 和 CMSIS-DAP。

## 已完成的离线验证

- `tools/Build.ps1`：0 警告，0 错误。
- `StudioX.DebugValidation --stm32`：23 包、244 基础型号、976 个模板的目标解析；244 × 2 工程类型 × 2 探针 = **976 份会话计划及 976 次 OpenOCD noinit 解析全部通过**。
- 每次解析对照包内目标脚本摘要，确认 CubeMX 内嵌脚本一致；假芯片 ID / Flash 容量不匹配时拒绝。
- M3 无 FPU、M4 含 FPU 的 MI 响应，包括稀疏 / 乱序 / 未命名寄存器，及公共栈帧、局部变量、观察值转换通过。
- 完整订货后缀与大小写归一化；未知型号、其他系列、错误架构、Flash/RAM 布局拒绝检查通过。
- 内置 ARM GDB 批处理接受 `hardware-breakpoint-limit unlimited`；没有连接远程调试端口。
- 原有 F407 离线状态机及全部特殊断点回归通过，包括资源不足、绑定回滚、日志自动继续、用户暂停优先和迟到事件清理。

结果路径（相对仓库根目录）：

- `.artifacts/stm32-debug-matrix-20260922/result.txt` 和 `matrix.json`；各型号目录保留两种探针的原始解析日志。
- `.artifacts/stm32-debug-common-20260922/result.txt` 和 `mi-transcript.txt`。
- `.artifacts/stm32-debug-build.log`、`.artifacts/stm32-debug-publish.log`。

## IDE 界面检查

通过 Computer Use 操作更新后的预览版，打开隔离工程 `.artifacts/F103-Debug-Review`（STM32F103C8 / HAL）。界面切换为 Cortex-M3，选择 DAP-Link，按 F7 编译成功，退出代码 0，生成 ELF / BIN / HEX / MAP。未点击下载或启动硬件调试。

从欢迎页切换到现有 F407 工程后，标题变为 Cortex-M4 / FPU；再切回 F103 后恢复 M3。展开调试窗口时显示「未连接调试目标」，寄存器没有伪造读数，运行 / 单步等操作在未连接时禁用。最终窗口停留在 F103 调试布局供查看。

## 尚待实板验证

其他型号的真实连接、断点命中、复位方式及结束后无需断电再下载，均需拿到对应板卡后测试。本次检查不能替代这些实测。既有 F407 物理结果见 [ST-Link 验收](F407-HARDWARE-ACCEPTANCE-20260921.md) 与 [DAP 验收](F407-DAP-ACCEPTANCE-20260922.md)。

本次未重新制作安装包；更新的是 `artifacts/MCU StudioX Debug Preview/MCU StudioX.exe` 预览版。

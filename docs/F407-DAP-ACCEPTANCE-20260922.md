# STM32F407 / CMSIS-DAP 实机验收（2026-09-22）

通过电脑操作技能在可见 IDE 中选择烧录器、连接、执行调试和下载。命令行仅用于构建 IDE、离线回归、读取 USB 描述及检查 IDE 已生成的日志，不用独立 OpenOCD 命令代替界面验收。

## 板卡和固件

- 用户确认 DAP 连接的是此前同一块 F407 板；本次识别为 PZ CMSIS-DAP，USB HID `C251:F001`，序列号 `0602A0002016`，OpenOCD 报告探针 FW 1.0。
- 目标 STM32F407ZG，Cortex-M4，器件 ID `0x10076413`，Flash 1024 KiB；SWD 2000 kHz。
- 工程 `.artifacts/F407-Hardware-Debug`，此前授权的最小 HAL 示例，8 MHz HSE / 168 MHz，不配置外部 IO。
- ELF SHA-256：`FCC9E4026BACCE431DCADB297FB53E8853B9F8779036B2A127A5BDE367868EB2`。
- 沿用此前原板固件备份 `.artifacts/hardware-original-backup/stm32f4-1MiB-20260921-215352.bin`。本次只下载同一最小示例，不执行整片擦除或选项字节写入。

## 界面通过项

1. 工具栏选择 `DAP-Link (CMSIS-DAP)`，设置按工程保存。调试状态与会话标记正确显示 DAP / STM32F407ZG。
2. 点击调试，IDE 编译并附加，板上固件与 ELF 校验通过；读取真实寄存器、调用栈和观察变量。
3. 点击运行，命中 `src/main.c:24`。首次观察 `app_counter=4271`、`app_input=175`、`app_output=349`，PC 对应 main。
4. 进入函数停在 `ComputeOutput:13`；逐过程到第 14 行；局部变量显示 `input=175`、`doubled=350`；跳出返回 main。
5. 复位并暂停停在 `Reset_Handler:61`，PC `0x080001e4`；继续后再次命中 main 断点。
6. **暂停状态结束 → 直接下载 → 重新调试 → 再次命中断点**，没有重新上电。下载退出代码 0，回读校验 1556 字节并复位运行。
7. 临时禁用 main 断点后连续运行，手动暂停成功，再恢复运行。
8. **运行状态结束 → 直接再次下载**，没有重新上电。日志确认先 interrupt，再移除断点、detach、退出 GDB，随后 OpenOCD 回报 `STUDIOX_DETACHED_RUNNING`。再次下载退出代码 0。
9. 测试后恢复 main 第 24 行断点；会话结束，目标运行，OpenOCD/GDB 子进程均已退出。

调试结束后的底部提示同步改为「调试已结束，烧录器已释放」，避免残留连接状态；清理失败仍保留真实错误。

## 日志

以下路径相对工程 `.build/`：

- 首次连接、单步、复位、暂停状态结束：`download-dd862f4af8bb42069b999427c7f89ae8/debug-session.log`。
- 首次再下载：`download-732b2d7ac3d0420b9b93249e922c3421/openocd.log`。
- 重连、运行/暂停、运行状态结束：`download-560602357d0e470fb6b11bd393655974/debug-session.log`。
- 运行状态结束后再下载：`download-33bed167375242a995e089c804a92889/openocd.log`。

## 覆盖范围

`tools/StudioX.DebugValidation` 的 DAP/ST-Link 会话计划、速度/序列号保存、接口匹配、非法配置拦截和原有普通/条件/计数/临时/日志断点回归均通过，记录在 `.artifacts/dap-debug-offline-2/`。构建 0 警告、0 错误。

本轮实机使用上述 PZ CMSIS-DAP HID 探针，验证的是普通断点、启停、单步、复位和释放链路。条件/计数/临时/日志断点沿用共用引擎，本轮只做离线回归；未声称逐项在 DAP 实板验收。USB bulk v2、其它 DAP 固件、热拔插和 RTOS 场景尚未在本轮实测。J-Link 当前仍只支持下载，未开放实机调试。

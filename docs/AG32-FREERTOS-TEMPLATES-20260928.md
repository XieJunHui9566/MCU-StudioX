# AG32 FreeRTOS 模板验证（2026-09-28）

## 入口修正

用户随后明确：所有特殊混合模式都应从普通 MCU 模板创建时勾选进入。因此当前器件包只展示一个 **FreeRTOS** 模板（`freertos-mcu`），复选框保持可选；未勾选生成普通 MCU，勾选后叠加 Verilog、内部回环入口及编译定义。模板不会自动勾选、禁用或在切换时改动用户选择。新版包 VF303 0.1.5，其余 0.1.3，声明合并旧 `freertos-mixed`，已建工程仍使用自己的资源副本。以下双模板记录保留为此前验证历史。

修正后验收：`artifacts/validation/ag32-rtos-checkbox/result.json` 共 116 项检查通过，覆盖七型号的勾选/未勾选创建、真实编译及 CCT6 联合构建。包保留策略 14 项回归通过；WPF 在所有模板之间切换验证复选框保持用户选择、旧包重新导入不恢复双模板，截图见 `artifacts/validation/ag32-rtos-checkbox-ui/`。基础构建 0 警告、0 错误；本次入口修正没有访问硬件。

四个 AGM 子系列包的七个精确型号新增 `freertos-mcu` 和 `freertos-mixed`，并保留原 `minimal`、`editor-demo`。新包版本 VF303 为 0.1.4，其余为 0.1.2；产品版本未改，未制作安装包或发布到 GitHub。

## 离线结果

- `artifacts/validation/ag32-rtos-final/result.json`：88 项检查通过。七型号 × 两模板均在含中文/空格路径中通过真实 CMake、GCC、链接，编译无警告。
- CCT6 常规模板以显式隔离夹具配置 HSE 8 MHz / SYS 200 MHz / BUS 100 MHz 和 GPIO4_4 → PIN2；联合构建 MCU 与基础映射通过。交付模板本身不预设外部晶振或物理引脚。
- CCT6 混合模板保持默认内部 HSI，内置 mapper + Supra 生成 99,944 字节逻辑镜像，全部工具阶段成功，最终阶段无警告。
- ELF/MAP 确认采用 FreeRTOS 的 `trap_entry`、系统层的实际时钟节拍初始化、heap_4 及系统构造函数；反汇编确认 f0 存储避开 `mepc` 槽位，含 fcsr 保存恢复。尚未据此宣称实板浮点切换正确。
- 原 `minimal` 模板真实 MCU + 映射构建回归通过，日志 `artifacts/validation/ag32-rtos-baremetal-build.log`。
- WPF `--preview-pack-catalog` 验证新版替代旧包、两模板自动锁定模式、切回裸机模板恢复手动选择，深浅主题完成截图；记录位于 `artifacts/validation/ag32-rtos-ui/`。
- `tools/Build.ps1 -BuildArtifactsDirectory artifacts/build/ag32-rtos`：0 警告、0 错误。包发布资源边界检查通过，仅验证资源，不制作发行包。

## 移植约束

AGM 本机 `framework-agrv_freertos/1.0.0` 的实际内核标记为 V11.1.0，厂商网页和芯片扩展的旧注释是 V10.4.6。采用本机锁定源码，文件哈希见 `examples/packs/agm.ag32-series/freertos/source-lock.json`；MIT 许可证复制到包及生成工程的 `vendor/licenses/`。

派生移植只调整节拍变量的运行期初始化、浮点额外上下文和外设中断包装入口，原始/派生哈希与变更说明写入 provenance。调度开始前读取实际 SYSCLK，包括 HSI 回退；调度后不能直接切换时钟。`Delay_us/ms` 继续使用 mcycle 忙等待，应用任务等待使用 `vTaskDelay`。

系统初始化、RTOS 钩子与定时器实现位于生成的 `StudioX_System.h/.c`，main 保持应用任务及创建/启动入口。混合演示预占内部 GPIO4_0/4_1，不映射到封装脚。

## 已连接设备的只读检查

用户随后接入实板，CMSIS-DAP HID 序列号 `0602A0002016`，SWD 1000 kHz。读得器件 ID `0x40200001`，物理 Flash 256 KiB，逻辑布局 `0x80027000 / 0x7ffd8fff`，符合现有 CCT6 目标检查。

运行中读取时厂商驱动报告 `Target not halted`，随后短暂停机完成完整备份并恢复运行。备份 `artifacts/validation/ag32-rtos-hardware-20260928/flash-before.bin`，262,144 字节，SHA-256 `81FD89C91A66545DD4609122F972D532AA2779B47701F67E665006E2E4B8BF09`。

原始选项读取仍报告此前已记录的 `option byte complement error`，读保护关闭、逻辑配置配对一致；未尝试修改或修复选项字节。原始日志与具体待测试镜像散列保存在同目录。当前只完成备份和连接检查，尚未写入 RTOS 测试固件，实板调度验收待用户确定写入及结束恢复策略。

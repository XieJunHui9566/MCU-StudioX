# CH32V307 编译与下载验证记录

2026-09-22。0.1.0 完成创建、语言服务和编译；0.1.1 完成 WCH-LinkE 实板下载和内存统计修正。
用户提供型号 CH32V307VCT6、外部晶振 8 MHz。

中断属性、硬件压栈与优化等级的后续核对见 [CH32V307 中断约定](CH32V307_INTERRUPTS.md)。中断实板运行不包含在此前最小固件下载验收中。

## 资源

- 本机 MounRiver Studio 2 / WCH，GCC12 12.2.0 v1.4、GDB 12.1、WCH OpenOCD 0.11.0+dev（2026-08-25）。
- 开发环境组件 `wch.riscv/1.0.0`，16,895 个文件有 SHA-256 清单；含完整 sysroot、multilib、GDB、binutils、CMake、Ninja、OpenOCD 和原始许可材料。
- 独立包 `wch.ch32v307/0.1.0`，285,258 字节；来源档案与三个 ZIP 固定哈希见制作脚本和包内 `vendor/provenance.json`。
- 包 SHA-256：`9fe950da564ff0d4dc1deab15e7ea0ea33dc1d7839a1c7942648349a51c7d7d7`。
- 官方资料入口：[WCH CH32V307 仓库](https://github.com/openwch/ch32v307)。未把 CH32V303/305/317 混入器件列表。

## 验证结果

| 项目 | 结果 |
| --- | --- |
| StudioX Release 构建 | 0 警告、0 错误 |
| VCT6 / RCT6 / WCU6 标准库模板 | 三种分别实际编译成功，产生 ELF/BIN/HEX/MAP |
| 工具与固件格式 | GDB 可启动；ELF32 RISC-V，入口 0，RVC / soft-float ABI |
| 时钟配置 | 官方 HSE × 18 分支；SystemCoreClock 初值 144,000,000。未测实板时钟 |
| 语言服务 | GPIO_SetBits 补全、RCC->CFGR0 成员、声明跳转、悬停、stdint.h 只读导航通过 |
| 原生界面 | 导入包 → 沁恒微电子 → VCT6 → 标准库 → 创建工程 → 点击编译，全流程成功 |
| Git | 界面创建的工程自动初始化仓库，编译输出在忽略规则中 |

最小模板 GNU ld 报告 Flash 1,548 B、RAM 2,080 B（含 2 KiB 栈），不是运行时峰值。
官方链接脚本把栈固定在 RAM 顶部。原构建分析器按最高地址统计，误把数据与栈之间空闲空间算入 RAM，显示 100%。现已合并 ELF PT_LOAD 实际分配区间，保留 2 KiB 栈，正确显示 RAM 2,080 / 65,536 B（3.17%）；Flash 1,548 / 262,144 B（0.59%）。独立段之间的空闲空间不计入，旧统计缓存自动重算。CH32V307 与 STM32F407 实际产物回归、重叠区间、越界段、固定顶部栈和旧缓存检查通过。

## 复验

```powershell
.\tools\Build.ps1 -Configuration Release
dotnet run --project tools/StudioX.WchValidation -c Release -- '<IDE 的 runtime 目录>' '<CH32V307 工程目录>' '<隔离缓存目录>'
```

界面验证工程：`.artifacts/CH32V307_UI_Test`。CLI 三型号日志：`.artifacts/ch32-build-vct2.log`、`ch32-build-RCT6.log`、`ch32-build-WCU6.log`。
器件包：`artifacts/packs/CH32V307-0.1.0/wch.ch32v307-0.1.0.mcupack`。
包已经通过 IDE 导入当前用户的 `MCUStudioX/packs` 目录。
发布脚本已加入该开发环境组件和 WCH 厂商目录；已有 0.2.0 安装包没有重新打包。

以上是 0.1.0 编译集成记录；该旧包没有 OpenOCD 配置，旧工程仍保持下载禁用，不自动替换已有器件配置。

## 0.1.1 下载与实机验收

- 新包：`artifacts/packs/CH32V307-0.1.1/wch.ch32v307-0.1.1.mcupack`。
- SHA-256：`39c6238c6eee516629474279b2696ff1177f4ac1f80f92637ded23a6a6df3ab3`。
- 开发环境组件保持 `wch.riscv/1.0.0`；VCT6 / RCT6 / WCU6 三模板实际编译与下载准备、Tcl 型号/划分/读保护拒绝检查均通过（不连接硬件）。
- 实机只验收 CH32V307VCT6 + WCH-LinkE，Windows 枚举 WCH-LinkRV，固件版本 2.16，RISC-V / SDI 6000 kHz。
- 型号 ID `0x30700568`（忽略修订位）；USER `0x40bf`，实际划分 256 KiB Flash / 64 KiB RAM，读保护关闭。未写选项字节、未自动解锁。
- 通过原生 IDE 打开 `.artifacts/CH32V307-Download-Test`，点击顶部「下载」完成编译、擦写、校验及运行。最终实现连续两次点击下载均成功，中间没有断电、拔插或手动复位。
- 下载按映射地址 0 写入 BIN，再从物理地址 `0x08000000` 独立读回。启用厂商 `page_erase`，最小固件实际擦写首 2 KiB；其余 Flash 与备份逐字节一致。原厂配置默认的全代码区擦除未使用。
- 原实现只执行 `reset run` 时，实测 PC 停在 0、时钟处于复位状态。WCH 分支已改成 `reset halt; resume`。随后读取 PC 为 `0x226`（反汇编对应 main 空循环）；重新连接仍为该地址。
- 复位并继续运行后，`SystemCoreClock` 为 144,000,000，RCC CTLR=`0x03038b83`、CFGR0=`0x0001040a`，HSE 与 PLL 就绪并选择 PLL。这是寄存器/软件频率验证，未用示波器测量晶振精度。
- 厂商 OpenOCD 在写入路径仍输出 `Unsupported register (enum gdb_regno)(8)`。保留原始日志；退出码 0、固件独立读回一致、复位后 main 正常运行。没有据此宣称交互式调试已适配。

最终下载日志：

- `.artifacts/CH32V307-Download-Test/.build/download-063e8bc269784e548830e32e7db44a91/openocd.log`
- `.artifacts/CH32V307-Download-Test/.build/download-63927602f64f45a8b0646392e68f7d46/openocd.log`

备份与读回（仅本地保存，不入 Git）：

- 原应用区 `.artifacts/ch32-original-256k.bin`，SHA-256 `7ba649069c94cda172ee1a88b7158ce35657e52572c39856d6dc91a7932d2dfd`；写入前已校验。
- 原额外物理 32 KiB `.artifacts/ch32-original-extra32k.bin`，SHA-256 `d7ebe81fbe74d9c73a31eb4de1727cfa096ee9877fd5497fe1894ac82a662670`。
- 测试固件 BIN 1,548 B，SHA-256 `688c6a35711e1519788608822b0cc8e71e905b41e58abc3db1af0cd0bc5829e1`。
- 最终只读复验 `.artifacts/ch32-final-readback.log` 和 `ch32-final-readback-288k.bin`；完成后复位并恢复运行。
- 原选项字节与下载后均为 `5aa5 40bf 00ff 00ff 00ff 00ff 00ff 00ff`。

Release 构建 0 警告、0 错误。新增包已导入本机；之前的 0.2.0 安装包未重新封装。RCT6/WCU6 仅离线验证，尚无实板；中断、外设、断点/单步/寄存器调试不在本次下载验收范围。随后开展的基本源码调试适配见 [CH32V307 调试验收](CH32V307-DEBUG-ACCEPTANCE-20260922.md)。

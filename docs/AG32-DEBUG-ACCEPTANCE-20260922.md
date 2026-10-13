# AG32VF303CCT6 / AGM BLASTER 调试适配记录

日期：2026-09-22。IDE 版本保持 0.1.0；器件包升级到 `studiox.preview.ag32vf303 0.1.1`。

结论：已有可接入的官方工具栈，本轮完成预览适配、离线检查和真实 IDE 界面的打开/编译检查。**本轮未连接、下载或调试 AG32 实板；实板调试验收仍待完成。** 用户照片确认外壳标识为 AGM DAP-LINK/USB BLASTER，不能据此确认设备当前固件模式、目标型号或接线。

## 实现

- AG32 工程仅提供“AGM BLASTER（官方）”，默认 1000 kHz、DAP / SWD 桥。使用 `agm.agrv/1.0.0` 中厂商修改的 OpenOCD 和 RISC-V GDB，不使用 ARM GDB 或通用 RISC-V OpenOCD。
- 原有调试状态机、普通/条件/命中次数/临时/日志断点、运行到光标、单步、调用栈、观察变量与只读内存接口复用。硬件断点数量和寄存器编号由实际目标返回；寄存器面板为 `AgRV · RV32 · 内核 / FPU`。
- 调试附加先检查器件和应用映像匹配，不自动下载；禁用 RAM 校验工作区，避免修改当前应用变量。
- GDB 附加时保存 AG32 调试控制寄存器，分离时恢复，恢复轮询并运行 CPU；只有 OpenOCD 明确报告运行状态才确认退出完成。继承现有探针互斥和进程清理流程，异常时不报告虚假成功。
- Flash 物理容量仍为 256 KiB。清单新增可选 `openOcd.applicationFlashBytes`，本包为 159744（156 KiB）；BIN 和 ELF 的实际装载范围都受此限制。ELF 必须与目标架构匹配。
- 连接时检查器件 ID `0x40200001`、256 KiB 容量、读保护，以及选项字节中逻辑起点 `0x80027000` 和反码。仅支持已核实的应用 156 KiB / 未压缩逻辑 100 KiB 布局；其他布局拒绝继续。不解锁、不修改选项字节、不下载逻辑区。

“官方”是本次支持范围和配置名称，不代表已通过 USB 身份认证硬件品牌。此版本不提供通用 DAP、ST-Link、J-Link 的 AG32 入口，也没有扩大到其他 AG32 型号。

## 验证证据

1. `tools/Build.ps1`：0 警告、0 错误。日志 `.artifacts/ag32-debug-final-build.log`。
2. 两个包模板 `editor-demo` / `minimal` 用内置厂商 GCC 实际编译，生成 ELF/BIN/HEX/MAP。RISC-V GDB 批处理接受全部不连接目标的初始化设置，读取 RV32 架构和 main 地址。GDB 仍有索引缓存目录不可确定的非致命警告，原始输出保留。
3. 官方 OpenOCD 在 `noinit` 模式解析实际调试计划；用 Tcl 模拟读数覆盖芯片、容量、读保护和逻辑区拒绝分支；模拟退出时确认调试控制恢复，目标仍暂停时明确失败。**这些模拟检查没有访问 USB，也不等同于实板执行。**
4. 检查超过应用区的 BIN、错误架构 ELF 即使哈希凭据一致也拒绝；通用 CMSIS-DAP / ST-Link / J-Link 不能启动此 AG32 调试配置。寄存器 MI 样本检查 PC、整数、FPU、CSR 的动态映射。
5. STM32 回归实际编译 F103/F407 的 CubeMX 与器件包工程，检查下载准备、调试 ELF 范围、接口配置和错误分支；公共调试操作和特殊断点离线测试通过。
6. **Computer Use 实际操作 WPF 界面**：打开 `.artifacts/AG32-Debug-Review`，展开烧录器列表核对唯一官方选项，点击“编译”，观察“编译成功，退出代码：0”。切换寄存器页和底部调试页，检查 AgRV/RV32 标题、调用栈、变量观察和源码布局。未点击“下载”或“调试”。界面构建日志位于该工程 `.build/studiox-build.log`。

结果目录：

- `.artifacts/ag32-debug-validation-20260922-final/result.txt`
- `.artifacts/ag32-stm32-download-regression/result.txt`
- `.artifacts/ag32-common-debug-regression/result.txt`

复现离线 AG32 检查（输出目录必须不存在）：

```powershell
dotnet run --project tools/StudioX.DebugValidation -- --ag32 artifacts/packs/studiox.preview.ag32vf303-0.1.1.mcupack 'artifacts/MCU StudioX Debug Preview/runtime' .artifacts/ag32-check-new
```

## 交付与后续实板验收

- 新包：`artifacts/packs/studiox.preview.ag32vf303-0.1.1.mcupack`。
- 包 SHA-256：`4312C2043108713EC09950061364497DD12A423822869825C10149CCEB5DCA7D`。
- 更新的可运行预览：`artifacts/MCU StudioX Debug Preview/MCU StudioX.exe`，其中 `device-packs/AGM` 附带新包，发布脚本也已改用新版包。本轮未重做安装程序。
- 旧 0.1.0 包没有硬件配置，旧工程不会静默升级；导入新包后新建测试工程。新包仍为本机 SDK 制作的临时开发材料，保留源码声明和来源，不在包内加入开发环境组件。

实板阶段仍须通过 IDE 确认实际连接、暂停/运行/单步、断点命中和特殊断点、栈帧/寄存器/变量/内存读取，以及“不重新上电：结束 → 下载 → 再调试”。尤其退出恢复目前只有离线验证，不能宣称已解决所有板上锁死情况。下载使用明确选定固件并保留已有备份、逻辑区与选项字节。

## 来源

- [AG32 官方开发说明](https://www.ag32mcu.com/dev-docs/doc_ag32_vscode_start/)：官方 DAP 下载器的上传及调试配置。
- [AG32 官方其他调试前端说明](https://www.ag32mcu.com/dev-docs/doc_ag32_other_cotex/)：厂商 SDK 调试工具接入说明。
- 本机 AgRV_pio 1.8.10 的 `platforms/AgRV/etc/agrv2k.cfg`；原始 SHA-256、修改范围和版本记录在包内 `debug/provenance.md`。

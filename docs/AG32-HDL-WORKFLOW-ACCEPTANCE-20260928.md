# AG32 联合构建与 RTL 仿真验收（2026-09-28）

## 本次交付

顶部编译 / F7 已联动 MCU GCC、VE 转换、AGM 原生 mapper 和 Supra 布局布线。顶部下载使用同一次构建的 MCU 与 FPGA 双镜像快照，并分别回读校验。AG32 自定义逻辑页面提供源文件、包含目录、宏、附加 SDC、静态时序报告和 RTL testbench 配置。内置 Icarus 生成 VCD，WPF 原生显示数字波形；Agent 共用构建与仿真应用服务。

工具来自已存在的本机分发包：Supra 2026.03.b0-a052d0a8（Yosys 0.61 / c18d47a44）、Icarus 14.0 devel。工程与运行时不含开发者绝对工具路径或私人 Supra 许可。各工具和资源有 SHA-256 索引。

## 软件与界面

- `tools/Build.ps1 -Configuration Release -BuildArtifactsDirectory artifacts/validation/ag32-pin-planning-build`：0 警告、0 错误。
- `tools/StudioX.HdlWorkflowValidation`：24 项通过。实际联合编译的夹具路径包含中文与空格；检查了 SDC、位流及工具锁定变化后的拒绝行为。
- 真实 testbench 验证 8 组组合输入、异步复位、寄存器采样和计数器；125 ns 正常结束，VCD 精度 1 ps，保留层级、总线、X、Z。
- `$fatal`、无限零时间循环超时、取消、非法顶层、越界路径、倒退 VCD 时间均明确失败；提前到达时间上限显示未完成全部断言的警告。源码变化标记历史波形。
- 真实 MCP 会话验证配置读取、Build 授权、未保存编辑保护，以及返回 VCD；仿真没有建立硬件会话。
- `--preview-hdl-workflow`：8 项真实 WPF 检查通过，包括入口、实际 Icarus 调用、波形、筛选、缩放、过期提示与普通工程隐藏。深浅主题截图位于 `artifacts/validation/hdl-workflow-ui-20260928/`。

## 实板联合下载

用户已授权使用连接的测试板。本次目标为 AG32VF303CCT6，CMSIS-DAP / SWD，1000 kHz；实际器件 ID 为 `0x40200001`。

| 镜像 | 地址 | 字节数 | SHA-256 |
|---|---|---:|---|
| MCU | `0x80000000` | 1,716 | `E39AB69C760FE8A5D2169820CD13E50F275A1F9466D533241DDF4FFD6985F45C` |
| 自定义 FPGA | `0x80027000` | 99,944 | `DC70E28FB6C678C3272E579E0A5D473753FEF86B402E674A0996140C30ABF2C0` |

先保存 262,144 字节完整 Flash 备份，SHA-256 为 `CDCA64537010D17697E08A1F0647BE59AF9D21182FC8A8333E8FD84C002F6D96`。随后通过产品 `OpenOcdService.DownloadApprovedAsync` 校验当前工程和批准的双镜像散列，执行目标身份检查、两段范围写入及两段回读校验，然后复位运行。OpenOCD 分别报告 verified 1,716 bytes / 99,944 bytes。

复位后内部 MCU↔FPGA 回环计数从 **2,259 增长到 4,345，errors 始终为 0**。组合逻辑、寄存器与计数器在板上持续运行；结束后 resume，保留测试固件运行。只读复核 `0x81000030` 的两字为 `80027000 7FFD8FFF`，与此前记录一致，未执行选项字节写入或整片擦除。

原始证据集中于 `artifacts/validation/hdl-workflow-20260928/`：`result.json`、`hardware-result.json`、`joint-build.log`、`joint-flash-before.bin`、两次 RAM 快照、`options-after.bin` 和 OpenOCD 日志。此前原始程序的备份仍在 `artifacts/validation/ag32-logic-hardware-20260928/flash-before.bin`。

## 时序范围

本夹具的 Supra 报告：最差 setup slack 4.866 ns，最差 hold slack 0.615 ns，覆盖 24 / 26 条连接（92.3%）。原始 setup / hold / Fmax / coverage 报告可在界面打开。未覆盖连接不能被表述为已满足时序，也未用此回环测试测量芯片最高运行频率。

当前波形为 **RTL 事件仿真**。尚未接入布局后 SDF 延时导出、厂商仿真单元库及反标校验；现有 Supra 命令检查不支持 `write_sdf`。静态时序报告、RTL 的 `#delay` 和真实硬件采样是不同证据，本次不宣称完成布局后延时仿真。

本次实板结果仅适用于上述 CCT6 内部回环，不扩展为其它封装、外接负载、复杂 IP 或其它探针的实板验收。此次没有发布安装包、修改产品版本或推送仓库。

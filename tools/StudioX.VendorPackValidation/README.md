# 跨厂商器件包离线验收

用内置工具链检查 StudioX 格式 1 的 ARM/RISC-V 器件包。该程序只使用文件、CMake/GCC/GDB 规划和 OpenOCD `noinit` 配置解析；不枚举或连接烧录器，不读写目标芯片。

```powershell
dotnet run --project tools/StudioX.VendorPackValidation -c Release -- `
  artifacts/tool-runtime `
  .artifacts/vendor-validation-new `
  artifacts/packs/Puya-0.1.1/puya.py32f030-0.1.1.mcupack `
  artifacts/packs/Puya-F403-0.1.0/puya.py32f403-0.1.0.mcupack
```

输出目录必须尚不存在。可一次传多个包，也可逐包运行以便定位失败。程序完整导入并验证包哈希，然后按精确型号核对 CPU/ABI、Flash/RAM 范围、链接脚本 `MEMORY` 起点和长度、启动文件与模板，并为每个型号至少创建一个工程。代表性编译按容量、CPU、启动/SDK 源文件和模板分组；生成 ELF/BIN/HEX 后检查装载段和入口。Cortex-M 另检查 SP/Reset 双向量；RISC-V 使用自己的 ELF 入口，不套用 ARM 向量规则。

声明 OpenOCD 的型号会检查下载配置、可用调试计划和每种探针的 `noinit` 脚本解析。某探针仅能下载时，`DebugPlans` 会少于 `Probes`；未声明 OpenOCD 的型号在 `matrix.json` 中显示 `HasOpenOcd=false`，IDE 下载/调试入口不应开放。脚本解析只确认配置加载及 `studiox_check_target` 过程存在，不能替代实板身份、读保护、擦写或断点验证。

`matrix.json` 逐组合记录结果、容量、编译产物大小、下载/调试能力和错误；`result.txt` 汇总数量。编译样本保留在 `projects/` 供复查，其余临时工程在完成检查后清理。

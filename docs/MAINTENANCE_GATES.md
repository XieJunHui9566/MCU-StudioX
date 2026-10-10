# 维护门槛

维护完成以当前源码、全部公共门禁、受影响领域的必跑验证及原始证据为准。基础编译成功、历史验收通过或检查数量增加，都不能替代漏跑的领域验证。产品版本、提交、安装包、发布和硬件操作继续遵守 [开发约定](../AGENTS.md)，运行维护验证不授权这些操作。

## 每次软件维护的公共门禁

所有软件改动运行 `tools/Test-Regression.ps1`，默认声明 `baseline`。公共门禁由 [机器清单](../tools/maintenance-gates.json)按名称要求，不以“至少跑了几个检查”判断完整性。

| 门禁 | 必须保住的行为 |
| --- | --- |
| `build`、`source-style` | `tools/Build.ps1` 成功；编译零错误、零警告；格式检查无变更，字面量语义保留 |
| `security-boundaries` | 路径、器件包、工具进程、插件容量及授权边界继续拒绝不合法输入，保留诊断 |
| `mcp-validation-build`、`mcp-contracts-and-boundaries` | 工具契约、设备连接及发送授权保持一致 |
| `StudioX.DesktopArchitectureChecks-build`、`StudioX.DesktopArchitectureChecks`、`StudioX.ArchitectureChecks-build`、`StudioX.ArchitectureChecks` | Desktop 调用应用服务；领域和扩展接口不依赖 WPF；插件隔离不退化 |
| `release-identity`、`release-pipeline-guards` | 普通维护不改版本、不自动打包或发布；原生命令失败不能被记作通过 |
| `maintenance-gate-contracts` | 缺项、失败、丢失日志、过期源码和未请求的真实验证不能满足维护门槛 |
| `debug-plugin-isolation` | 调试插件的授权、生命周期及退出边界保持有效 |
| `keil-migration-workflow` | 迁移预览、应用级宿主、取消、切换工程、打开与定位、诊断过期及安装渲染器继续可用 |
| `project-file-synchronization` | 外部、IDE、插件、AI 写入共用同步；冲突保留缓冲区和保存基线；删除恢复不覆盖重现文件；跨父目录移动、快开、通知丢失和工程切换有效 |
| `source-registration` | 字面量原生 CMake / IDF SRCS 的目标和组件归属明确；预览保留未保存内容，过期/复杂/只读配置拒绝，一次撤销及真实 WPF 有效 |
| `openocd-plot-offline` | 离线调试状态与绘图边界有效；模拟结果不能表述为实板结果 |
| `code-templates`、`code-template-ui` | 模板变量、范围、一次撤销以及真实 WPF 展示继续有效 |
| `peripheral-development` | 外设生成依赖明确，缺少 SDK 证据时拒绝猜测接口 |
| `fault-peripherals`、`fault-peripheral-ui` | 解析范围、SVD 边界、当前值/复位值区分以及深浅主题界面有效 |
| `product-workflows` | 新建、导入、组件及插件工作流保持有效 |

未提供 `-SamplePlugin` 时还运行 `sample-plugin`，生成独立开发插件；提供现有插件时由 `product-workflows` 验证该输入。当前默认执行 24 个步骤；数字只是运行规模，完整性按机器清单的名称判断。

## 按变更领域追加必跑验证

维护者按实际影响声明 `-MaintenanceAreas`，可同时选择多个领域；脚本总会加入 `baseline`。公共代码、契约或服务变更影响多个领域时必须全选。纯说明文字改动可采用 `baseline`；运行命令、机器门槛和证据规则有变更时仍须实际验证受影响入口。

| 触发变更 | 声明领域 | 必需的已有输入 | 追加门槛 |
| --- | --- | --- | --- |
| 编辑器、工程文件同步、搜索替换、草稿恢复、C/C++ 诊断或语言配置 | `editor` | `-LanguageRuntime` | `live-diagnostic-reliability`、`live-diagnostic-ui`；同步报告中的 `language=passed`；真实 clangd 和真实 WPF 均通过 |
| Keil 解析、迁移生成、移植构建、迁移插件及诊断记录 | `keil`；同时影响分析时加 `editor` | `-KeilValidationInputs` | 迁移报告 `realCompilation=passed`；F407 HAL / F103 SPL 的原工程只读迁移、实际编译及原工程不变检查通过 |
| 源码登记解析、变更建议、编辑应用或编译列表联动 | `sources,editor` | `-SourceRegistrationInputs`、`-LanguageRuntime` | 源码登记报告 `realCompilation=passed`；真实原生 MCU / ESP-IDF 的加入、改名、移除均编译链接成功，当前数据库无旧路径残留，源文件与未保存内容保留 |
| 组件安装、租约、恢复、工具选择及工程健康 | `environment` | `-ProjectHealthNinja`、`-EnvironmentReliabilityNinja` | 工程健康编译/分析、组件进程终止后的恢复边界通过 |
| ESP-IDF 外设代码、依赖、SDK 版本适配或外设页面 | `peripheral`；涉及语言诊断加 `editor` | `-PeripheralRuntime`、`-PeripheralPackInputs` | 实际工程编译、`peripheral-development-diagnostics` 和 `peripheral-development-ui` 通过，版本/目标来自明确输入 |
| 故障解码、SVD、GDB 工具选择或转储分析 | `fault` | `-VendorSvd`、`-ToolsetsDirectory`、`-CoreDumpFixtures` | `fault-peripherals` 使用厂商 SVD 和已有官方转储实际解码；寄存器界面门禁通过 |

领域必需输入缺失时，在构建前失败；不得删掉声明来把未验证领域记为完成。所有报告必须声明当次领域，不能事后把基础回归扩写成真实编译或语言验收。额外传入可选工具参数仍会运行对应验证，但正式领域结论要在启动时声明。

以下验证保持独立，需要随受影响改动附上新的专项证据；上述领域报告不替代它们：

公共门禁还包括 `pack-retention-build`、`pack-retention`、`pack-catalog-ui` 和 `editable-combo-ui`。它们分别保留覆盖清理与损坏保护、阻塞后台时器件仍可选择，以及 STC 可编辑串口在深浅主题下的选中显示、手输和刷新保留检查。UI 使用独立用户目录与小型夹具，不打开串口；普通选择框也必须保留原来的选中项显示。

包 ID 为目录联接时，目录读取在清理开始前抛出 `PATH_LINK`。相应夹具验证原始错误包含该 ID，并重新完整校验外部目标的两个版本；此前预期逐项清理失败报告的断言改为这一实际拒绝行为，外部资源保留检查继续执行。

| 触发变更 | 独立必需证据 |
| --- | --- |
| 型号、容量、时钟、启动/链接文件、HAL/SPL/RTOS 模板 | 新格式包全量哈希及身份核对、相关型号/模板的实际生成与编译；例如 `Test-Stm32LibraryMatrix.ps1`、`Test-Arm32Expansion.ps1` 或对应包验证器。报告必须列明实际覆盖型号与模板 |
| SDK/工具版本、编译参数、数据库或跨目录迁移 | 受影响版本/目标的真实编译和 clangd；可使用 `-LanguageValidationMatrix`、`-F407Pack` 与 `-ToolsetsDirectory`，需要迁移时补 `StudioX.EspressifValidation --portability` 的证据 |
| 自包含载荷、安装、升级、卸载或环境依赖 | [P0 独立验收](P0_ACCEPTANCE.md)和对应安装器验收；干净 Windows、旧版升级、组件/用户资料保留及持久环境比较。普通回归不替代新的安装包验收 |
| 调试、下载、串口、RTOS 或器件硬件行为 | 相关离线状态机与边界检查；只有当次明确授权、已确认型号/板卡/固件时才追加实板验收，保留备份和原始硬件日志 |

没有所需专项材料时应写“该项待验证”，限制本次交付范围，不宣称完整领域或实板验收。历史硬件授权、旧版安装包和旧 SDK 的成功记录不扩大新一轮范围。

## 执行与复核

开发机使用 PowerShell 7、现有 .NET 10 SDK 和已准备的运行时；这些命令不下载 SDK、不连接硬件、不改全局 PATH。输出放在新的 `.artifacts` 子目录或源码仓库之外，避免验证产物变成源码摘要的一部分。输入可用本机已有路径，不能把开发者绝对路径写进工程、工具锁或机器清单。

```powershell
# 公共软件门禁；缺少真实语言或迁移输入时，只能形成 baseline 结论。
./tools/Test-Regression.ps1 -OutputDirectory .artifacts/maintenance-baseline-<新编号>

# 编辑与 Keil 同时有改动时，使用已准备的运行时和明确的迁移输入。
./tools/Test-Regression.ps1 `
  -OutputDirectory .artifacts/maintenance-editor-keil-<新编号> `
  -MaintenanceAreas editor,keil `
  -LanguageRuntime <已有runtime目录> `
  -KeilValidationInputs <本机已有输入JSON>

# 在复核或准备交付时重新确认源码仍与报告一致，不重复启动编译。
./tools/Test-MaintenanceEvidence.ps1 `
  -RegressionReport <本次regression.json> `
  -MaintenanceAreas editor,keil
```

Keil 输入 JSON 的字段为 `cli`、`packs`、`toolsets`、`f407Project`、`f103Project`，指向明确已有的 CLI、器件包、工具集与两个原工程；参考 [Keil 验证入口](../tools/Test-KeilWorkflow.ps1)。其它领域参数与专项说明见 [回归与发行流程](REGRESSION_RELEASE.md)。

源码登记使用 `-MaintenanceAreas editor,sources -LanguageRuntime <已有runtime> -SourceRegistrationInputs <本机输入JSON>`；字段与操作范围见 [源码登记](SOURCE_REGISTRATION.md)。该领域必须有两类真实构建证据，离线预览的 `not_requested` 不能满足维护门槛。

## 通过、失败与证据要求

1. 当次进程成功退出，`regression.json.passed=true` 且 `maintenance.passed=true`。所有机器清单要求的门禁各有唯一通过记录；已有失败记录也不能被忽略。
2. 每项原始日志存在，指定的 JSON 摘要明确成功；真实语言/编译不能是 `not_requested`。UI 超时、退出异常、坏 JSON、缺失证据均失败。
3. 回归前后的源码提交与内容一致，并在复核时仍一致。摘要包含 Git 可见的跟踪及未忽略文件内容，也包含当前未提交改动；构建产物、运行时缓存及 `.artifacts` 不参与。`HEAD` 相同且 `sourceDirty=true` 不足以复用旧结论。文件型输入另记录前后 SHA-256 并在复核时比较，包括 Keil 输入 JSON、Ninja、SVD 和外设输入计划。复核工具不全量重读外部 SDK、器件包及原工程目录，其具体身份与内容须按专项报告的版本、哈希和原工程不变证据核对。
4. 保存声明领域、明确输入、源码摘要、每步日志、原始工具输出、测试摘要及 UI 截图；实际编译/器件包另保留目标、版本和哈希。CI 默认只跑公共软件门禁，保留证据 14 天；正式本地验收证据由维护者按工程归档。
5. 失败先查对应 `*.log` 和 JSON 中的原始诊断，修复后在新目录重跑。相关源码或输入改变使旧结论失效；不覆盖失败日志、不删门禁、不修改通过标志、不降低断言来换取绿色结果。只有纯复核且源码/输入未变时才能使用本次已有记录。

修订门槛时，机器清单、触发表和验证入口应一起更新。删除或替换既有行为检查必须说明原行为如何获得等价覆盖，不能只调低检查数量。`Test-MaintenanceGates.ps1` 是证据规则的夹具验证，不冒充 clangd、GCC、设备或安装器验收。

CI 保存隔离验证目录中的日志、摘要、截图及 `.studiox` 夹具元数据，排除构建目录、空运行时和 `.git`；包含隐藏文件与排除模式采用 [upload-artifact v4 的明确配置](https://github.com/actions/upload-artifact/blob/v4/README.md#uploading-hidden-files)。工作流修改尚未提交时，只能声明本地验证，不能声明远端 CI 已运行。

## 已有记录如何使用

2026-10-08 的工程同步验收有 24 个统一回归步骤、31 项应用/语言检查、22 项同步 WPF 检查；通用编辑工作区另有 130 项语言检查、18 项 WPF 检查，Keil 实际迁移有 82 项检查。这些数值是当日覆盖记录，后续可以增加，不能用总数替代具名门禁与领域证据。

当日旧报告只绑定提交和 `sourceDirty`，新门槛因此把它们视为历史证据。维护门槛落地后的新报告追加源码前后摘要和声明领域，`formatVersion=1` 保持与既有发行读取器兼容。历史虚拟机与硬件记录的版本、输入和范围分别见对应验收文档，不表述为当前全部源码都已重新验收。

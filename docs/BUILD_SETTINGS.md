# 工程编译参数

打开工程后的“器件与模板”页面包含可见的“编译参数”卡片。按当前后端选择参数并点击“保存参数”，下一次编译或下载前的自动编译使用所选值。恢复工程默认也需保存。下载连接、时钟、硬件及调试配套在下载按钮旁的独立设置页，不把内存模型和优化选择移入该页面。

## 后端与保存位置

以下按 2026-11-17 源码核对，具体选项以当前工程与锁定工具能力为准。

| 后端 | 编译参数页 | 保存位置与边界 |
|---|---|---|
| 使用通用参数服务的原生 / CubeMX / SDK 工程 | 优化等级、调试信息 | `.studiox/build.json`；无覆盖项时沿用工程默认 |
| STC 8 位 SDCC | 默认、低优化、体积优先、速度优先；small / medium / large；内部 RAM（IDATA 总容量）与 XDATA 大小 | `.studiox/build.json`；容量按准确器件与模板校验，空值沿用包配置；Mon51 资源边界仍有效 |
| MX251 | small / large、体积优先 / 速度优先、C 语言标准 | 统一写入 `mx251.json` 的 `model`、`optimization`、`standard`，独立 CLI 使用相同配置 |
| PIC、Arduino、Zephyr | 尚未接入这套通用编译参数卡片 | 使用各自的 CMake / SDK / sketch 配置入口，由对应后端解释参数 |

SDCC 低优化只关闭选定优化过程，不等于 GCC `-O0`；标准调试信息使用 CDB，当前不提供 GCC `-g3` 档位。RT51 当前模板要求 large，Mon51 的调试信息与容量设置在下载配置页保存，继续通过同一构建服务校验。

MX251 的 C 标准可选 `default`、`c89`、`c99`、`c11`、`c17`、`sdcc89`、`sdcc99`、`sdcc11`；语言开关不扩大编译器已经验证的支持范围。恢复工程默认读取创建工程时随附的模板配置，同时恢复模型、优化和标准。`mx251.json` 在编辑器中有未保存内容或读取后被外部修改时，先处理该文件再保存页面参数；保存变更会使旧构建凭据和占用失效。运行库与模型组合仍须兼容，见 [MX251 适配](MX251.md)。

当前通用服务保存一套工程覆盖项；CubeMX 可使用导入时选定的 CMake 预设。跨后端的命名 Debug / Release / 测试配置切换尚未接入，不能将单项优化档位解释为完整构建配置。

## GCC 风格选项

下表仅说明适用后端的 GCC 风格参数，不用于 SDCC、MX251 或 PIC。

| 设置 | 可选值 |
| --- | --- |
| 优化等级 | 工程默认、`-O0` 不优化、`-Og` 适合调试、`-O1` 基础优化、`-O2` 速度优化、`-O3` 更高速度优化、`-Os` 优先减小体积 |
| 调试信息 | 工程默认、`-g0` 不生成、`-g2` 标准信息、`-g3` 包含宏信息 |

源码调试建议 `-Og` 和 `-g3`；更高优化可能导致变量被消除、源码行与单步顺序不同。显式选择 `-g0` 后，实机调试入口提示恢复调试信息并重新编译、下载，不启动硬件连接。

CH32V307 的中断入口仍需匹配启动配置的 WCH 中断属性，优化等级不能替代该声明，见 [CH32V307 中断约定](CH32V307_INTERRUPTS.md)。

设置独立保存在工程的 `.studiox/build.json`，例如：

```json
{
  "formatVersion": 1,
  "optimization": "Og",
  "debugInfo": "Full"
}
```

默认值为 `ProjectDefault`；调试信息其他值为 `None`、`Standard`。不支持的版本或枚举报错。保存与构建使用同一个互斥门；UI 在构建、下载、调试等操作期间禁用修改。更改设置会清除旧构建凭据和占用统计，并在下次配置重建 CMake 缓存；调试源码指纹也包含该文件，防止外部改动后误用旧 ELF。

## CMake 集成

此节说明使用 `ProjectBuildSettings` 的原生 Pack / CubeMX CMake 参数注入路径；MX251 使用自身的 `mx251.json` 与 SDK 构建契约，PIC、Arduino、Zephyr 不使用该通用注入。Engine 在 `.build` 生成初始化脚本，经 `-C` 追加到已有 `CMAKE_PROJECT_INCLUDE` 列表。现有预设的 project include 保留。注入的 C/C++/ASM 编译规则在 `<FLAGS>` 之后追加所选参数，让其覆盖 SDK、目标和源文件上已有的优化/调试参数；链接规则也传递这些参数，供链接阶段使用。没有选择的类别不追加覆盖参数。不改写根 CMake、预设、SDK 或内部器件配置。

配置成功后，检查 `compile_commands.json` 中实际命令的最终优化和调试选项。自定义规则导致参数未生效时返回 `BUILD_SETTINGS_NOT_APPLIED`，保留配置日志并停止编译。外部构建系统、自定义命令或构建时另行启动的子工程不在此支持范围；需要时选择工程默认并维护其自身 CMake。恢复默认通过全新配置移除旧注入。

实现依据：[CMAKE_PROJECT_INCLUDE](https://cmake.org/cmake/help/latest/variable/CMAKE_PROJECT_INCLUDE.html)、[CMAKE_LANG_COMPILE_OBJECT](https://cmake.org/cmake/help/latest/variable/CMAKE_LANG_COMPILE_OBJECT.html) 和 [CMake 命令行参数](https://cmake.org/cmake/help/latest/manual/cmake.1.html)。

## 验证

- `dotnet run --project tools/StudioX.BuildSettingsValidation -c Release -- <tool-runtime> <packs> <新输出目录>`：在隔离工程实编 STM32F103、CH32V307 及 CubeMX 风格导入工程；核对 C/C++/ASM、目标与源文件选项冲突、带空格路径、重复配置、恢复默认、持久化、构建凭据失效、`-g0` 产物无 `.debug_info`、保留预设 include 和源 CMake。
- `MCU StudioX.exe --preview-project <新输出目录> <工程目录>`：仅修改工程副本，验证控件保存、重新打开、恢复默认、未保存源码不变，以及深浅主题和窄窗口布局。输出图片及 `build-settings-result.txt`。
- 不访问硬件。生成的临时工程与 SDK 副本放在 `.artifacts`，验收后保留日志、结果和图片并清理副本。

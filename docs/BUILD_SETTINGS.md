# 工程编译参数

打开工程后的“器件与模板”页面包含“编译参数”卡片。选择优化等级、调试信息，再点击“保存参数”，下一次编译或下载前的自动编译使用所选值。恢复工程默认也需点击保存。现有工程没有配置文件时全部沿用工程默认，不改变原有构建行为。

| 设置 | 可选值 |
| --- | --- |
| 优化等级 | 工程默认、`-O0` 不优化、`-Og` 适合调试、`-O1` 基础优化、`-O2` 速度优化、`-O3` 更高速度优化、`-Os` 优先减小体积 |
| 调试信息 | 工程默认、`-g0` 不生成、`-g2` 标准信息、`-g3` 包含宏信息 |

源码调试建议 `-Og` 和 `-g3`；更高优化可能导致变量被消除、源码行与单步顺序不同。显式选择 `-g0` 后，实机调试入口提示恢复调试信息并重新编译、下载，不启动硬件连接。

CH32V307 的中断入口仍需匹配启动配置的 WCH 中断属性，优化等级不能替代该声明，见 CH32V307 中断约定。

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

支持 Pack 生成工程和 CubeMX 导入工程。Engine 在 `.build` 生成初始化脚本，经 `-C` 追加到已有 `CMAKE_PROJECT_INCLUDE` 列表。现有预设的 project include 保留。注入的 C/C++/ASM 编译规则在 `<FLAGS>` 之后追加所选参数，让其覆盖 SDK、目标和源文件上已有的优化/调试参数；链接规则也传递这些参数，供链接阶段使用。没有选择的类别不追加覆盖参数。不改写根 CMake、预设、SDK 或内部器件配置。

配置成功后，检查 `compile_commands.json` 中实际命令的最终优化和调试选项。自定义规则导致参数未生效时返回 `BUILD_SETTINGS_NOT_APPLIED`，保留配置日志并停止编译。外部构建系统、自定义命令或构建时另行启动的子工程不在此支持范围；需要时选择工程默认并维护其自身 CMake。恢复默认通过全新配置移除旧注入。

实现依据：[CMAKE_PROJECT_INCLUDE](https://cmake.org/cmake/help/latest/variable/CMAKE_PROJECT_INCLUDE.html)、[CMAKE_LANG_COMPILE_OBJECT](https://cmake.org/cmake/help/latest/variable/CMAKE_LANG_COMPILE_OBJECT.html) 和 [CMake 命令行参数](https://cmake.org/cmake/help/latest/manual/cmake.1.html)。

## 验证

- `dotnet run --project tools/StudioX.BuildSettingsValidation -c Release -- <tool-runtime> <packs> <新输出目录>`：在隔离工程实编 STM32F103、CH32V307 及 CubeMX 风格导入工程；核对 C/C++/ASM、目标与源文件选项冲突、带空格路径、重复配置、恢复默认、持久化、构建凭据失效、`-g0` 产物无 `.debug_info`、保留预设 include 和源 CMake。
- `MCU StudioX.exe --preview-project <新输出目录> <工程目录>`：仅修改工程副本，验证控件保存、重新打开、恢复默认、未保存源码不变，以及深浅主题和窄窗口布局。输出图片及 `build-settings-result.txt`。
- 不访问硬件。生成的临时工程与 SDK 副本放在 `.artifacts`，验收后保留日志、结果和图片并清理副本。

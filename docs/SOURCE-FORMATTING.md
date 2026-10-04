# 第一方源码排版

## Python、PowerShell 与 C

使用固定版本的 Black 24.10.0、PSScriptAnalyzer 1.24.0 和 clang-format 18.1.8。工具仅放在 `artifacts/validation/source-style-current`，可随验证产物清理；不安装到用户配置，不复制 MCU SDK 或编译链。下载地址、版本与 SHA-256 固定在 `tools/source-style-tools.json`，上游许可证随工具保留。

首次准备并检查：

```powershell
./tools/Format-FirstPartySources.ps1 -PrepareTools
```

应用排版后，再运行一次只读检查：

```powershell
./tools/Format-FirstPartySources.ps1 -Write
./tools/Format-FirstPartySources.ps1
```

不带 `-Write` 时发现排版差异会返回非零退出码。只有显式传入 `-PrepareTools` 才下载工具。Python 可用 `-PythonExecutable` 指定，默认使用现有 `python` 命令。

覆盖范围：

- `tools` 根目录的 Python、PowerShell 维护脚本、`tools/acceptance` 来宾验收脚本，以及 STC 的离线检查与运行时保护脚本。来宾脚本保留 UTF-8 BOM，并用 Windows PowerShell 5.1 检查语法。
- `examples/packs` 内本仓库维护的 C/H 模板。
- 原生 LVGL PC 宿主、STC 启动器、第一方组件头文件和离线 C 验证夹具。`Resources/Ag32/Peripherals` 的厂商驱动及带占位符的 `StudioX_System` 生成模板由维护者单独核对。

默认四空格缩进，控制块展开显示；C 的花括号单独成行，头文件顺序保持不变。Python 和 C 的行长目标为 100，生成字符串和外部协议文案不为排版强行折断。中文注释说明时序、来源、失败原因与职责边界；许可证和上游声明保持原文。

Black 使用保留字符串拼写的选项，并在内置检查之外核对完整 Python AST；包脚本里的 C、汇编和链接脚本字符串也必须保持相同值。[Black 官方用法](https://black.readthedocs.io/en/stable/usage_and_configuration/the_basics.html)

C 按 `.clang-format` 排版后核对字符串与非注释 token 的原有顺序。PowerShell 先用系统解析器确认语法，再检查非注释 token 与字符串完全相同；清除行尾空格时跳过 here-string 的内容。格式器只运行空白、缩进和花括号位置规则，不改命令大小写。[clang-format 风格选项](https://clang.llvm.org/docs/ClangFormatStyleOptions.html)、[PSScriptAnalyzer 固定版本](https://github.com/PowerShell/PSScriptAnalyzer/releases/tag/1.24.0)

厂商 SDK、生成产物和内嵌生成字符串不在修改范围内。安装脚本的 Pascal 区域按相同的块布局人工整理，并核对 token；安装信息、产品身份和版本宏保持原值，不借排版执行安装包构建。

## 验证材料

`artifacts/validation/source-style-current` 保存每个文件的 AST/token 等价结果、工具来源和摘要。原生 LVGL 宿主与 STC 启动器另以已有的 MinGW GCC 做 `-Wall -Wextra -Werror -fsyntax-only` 检查；语法检查不连接设备，也不写固件。

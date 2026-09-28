# Python 3 编辑准备

树莓派 MicroPython 工程另有明确的板型 API、REPL 与脚本上传支持，见 [MicroPython 适配](MICROPYTHON.md)。本文其余内容描述通用 Python 文件编辑能力。

IDE 可编辑已有工程中的 `.py`、`.pyw` 和 `.pyi` 文件。在工程树中新建文件并填写这些扩展名即可，或通过已有的 `--open <工程目录> --files scripts/tool.py` 打开。文件图标显示 `PY`，状态栏显示 `Python`。

## 已接入的编辑功能

- 深色、浅色主题高亮：关键字、装饰器、函数调用、数字、`#` 注释、普通字符串和多行三引号字符串；识别字符串前缀。
- 自动补全与 `Ctrl+Space`：Python 3 关键字、`match`/`case` 软关键字、常用内置函数、当前未保存文件内的标识符、函数和类名。Python 名称区分大小写。
- `Tab` / `Enter` 插入、`Esc` 关闭；函数自动补入 `()`，已有括号不重复插入，词中补全覆盖整个名称，支持单步撤销。
- 输入 `(`、`,`、`)`、`=` 或按 `Ctrl+Shift+Space`，显示常用内置函数和当前文件普通 `def` / `async def` 声明的参数。能够跳过嵌套括号和字符串中的逗号，并识别关键字参数。
- `Ctrl+/` 使用 `#` 切换行注释，保留已有缩进。
- `F12` / `Ctrl+F12` 转到定义 / 声明，`Alt+←` 返回；`Shift+F12` 或右键「查找引用」显示工程内的位置列表，双击定位。
- `Ctrl+F` 查找、`Ctrl+H` 替换，`F3` / `Shift+F3` 前后匹配；支持大小写、全词和正则。当前文件全部替换作为一次撤销操作，不直接保存磁盘，也不修改其他文件。
- 注释和字符串中抑制补全；编辑、移动光标、切换文件后取消旧请求，防止把提示插入另一份文档。

## 边界与后续接入

本阶段是 C# 实现的离线编辑辅助，不依赖 Python 解释器、clangd、Node 或网络。不会运行脚本或扫描、安装用户环境。输入基于当前文档快照，最多处理 1,048,576 个 UTF-16 字符，单次最多显示 200 个候选。

当前文件符号仅按词法收集，尚不解析作用域、继承、返回类型、第三方库或跨文件导入；模块导入语句和未知对象的属性不提供推测性候选。常用内置函数提示显示简化调用形式，未覆盖所有重载。装饰器、lambda、泛型函数声明及复杂默认表达式还没有完整语义分析。普通多行字符串支持高亮；f-string / t-string 插值内暂不提供表达式补全，其高亮仍按字符串规则处理。

导航通过独立的静态索引解析普通函数、类、参数、直接赋值、显式导入与别名、工程根目录和 `lib/` 中的模块、包内相对导入、`global` / `nonlocal`。引用结果包含定义与导入，排除注释、字符串和可辨别的同名局部绑定；已打开文件使用未保存缓冲区。MicroPython 固定目录 API（如 `machine.Pin`、直接构造的 `led.toggle`）跳转到只读声明，不冒充固件源码。

导航不执行 Python，不推断动态导入、继承、复杂赋值、函数返回对象或 f-string 内表达式。扫描忽略虚拟环境、器件 SDK 与链接目录；上限为 512 个文件、单文件 1 MiB、合计 8 MiB 文本，超限明确报错。

通用 Python 工程模板、解释器选择、虚拟环境、包管理、运行、调试与类型诊断尚未接入。F7 仍使用当前 MCU 工程的构建配置。通用 Python 3 编辑支持不表示已提供 MicroPython 固件或板级 API。

后续语言服务可以在 Application 层替换 `PythonAssistanceService` 的实现，复用 `CodeSuggestion` / `CodeSignature` 与桌面的取消、插入和撤销机制。运行环境与工程配置应独立建模，不从文件后缀推断解释器或器件身份。

## 验证

```powershell
./tools/Build.ps1
dotnet run --project tools/StudioX.PythonValidation -- artifacts/validation/python-editor-20260928/offline.txt
dotnet run --project tools/StudioX.PythonNavigationValidation -- '<新的验证目录>'
& './src/StudioX.Desktop/bin/Debug/net10.0-windows/MCU StudioX.exe' --preview-python artifacts/validation/python-editor-20260928/ui
```

离线检查覆盖三引号、转义、CRLF、Unicode 名称、大小写、嵌套调用、词中替换范围、取消与大文本保护，并实际运行 AvalonEdit 的逐行高亮。桌面预览使用独立用户目录和内存文档，验证没有 clangd 会话时的自动补全、Tab、撤销、参数弹窗、文件切换、注释及两套主题；不会保存工程源码。

规则参考：[Python 3 词法说明](https://docs.python.org/3/reference/lexical_analysis.html)、[内置函数说明](https://docs.python.org/3/library/functions.html)。

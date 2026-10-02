# 常用代码模板

通用文本模板属于 Application 编辑服务，不依赖芯片、SDK、WPF 或插件执行宿主。Desktop 负责模板管理、变量表单和 AvalonEdit 单次可撤销插入。借鉴 [Keil Text Completion](https://www.keil.com/support/man/docs/uv4/uv4_dg_template.asp) 的保存与调用方式；不引入 Keil 模板兼容层或执行语言。

## 使用

- `Ctrl+Alt+T` / 编辑菜单 / 命令面板打开模板管理。按语言筛选，搜索名称、缩写、说明；右侧预览正文。双击或插入按钮调用。
- 源码选区右键“将选区保存为代码模板…”自动去基础缩进并转义 `$`；不会写入源码。填写名称、缩写、适用语言和存储位置后保存。
- 输入缩写并按 `Ctrl+Space`，模板与现有语言提示一起列出。手动补全在没有语言服务时仍可使用模板；自动语言提示沿用已有流程。
- 有变量时先填写并预览，同名字段只填一次。插入替换选区或补全缩写，按目标文件缩进及首个换行类型排版。只改未保存缓冲区，单次撤销/重做。
- 内置 C/C++ 条件、循环、函数、头文件保护，以及 Python 函数、CMake 源列表可以复制后定制。

## 语法

`${name:default}` 定义单行字段，`${name}` 引用；默认值可为空，重复默认值须一致。名称为 ASCII 字母/下划线开头的标识符，最长 48 字符。默认值不含换行或 `{`。

`${cursor}` 唯一光标标记，省略时在末尾；`${selection}` 展开当前选区并保留相对缩进；`${fileName}` / `${fileStem}` 为文件名/无扩展名文件名。内置字段不能带默认值。`$$` 是普通 `$`，所以 CMake 原生变量写作 `$${PROJECT_SOURCE_DIR}`。反斜线直接保留。填写值不重新解析，不能执行脚本或访问环境变量。

当前不改变 clangd LSP 的 `snippetSupport=false`，不声称支持 LSP tab-stop 或正则变换。变量通过明确表单填写。

## 存储与冲突

个人库位于独立用户数据目录 `code-templates/templates.json`；共享库为工程 `.studiox/code-templates.json`。后者可纳入源码版本管理，`.lock` 和 `.tmp-*` 不应提交。内置模板只读，不写入这些库。同库同语言缩写忽略大小写去重，不同来源的缩写保留来源标签，由用户选择。

JSON 格式为 `formatVersion: 1` 和 `templates` 数组，每项包含 `id`（N 格式 GUID）、`name`、`shortcut`、`language`、`description`、`body`。语言允许值由 `CodeTemplateService.Languages` 提供。正文 65,536 字符、32 字段、展开结果 262,144 字符；每库最多 500 项、文件最多 4 MiB。

读取 SHA-256 基线；保存持有跨进程文件锁后重新校验基线，临时文件原子替换。坏 JSON、未来格式、超限和外部修改均显式报错并保留源文件；Desktop 保留完整异常到日志。项目路径通过 PathBoundary，拒绝内部重解析点。弹窗与补全捕获文档、版本、工程、光标和选区，状态改变或文件变只读时拒绝修改。

## 验证

基础构建：`tools/Build.ps1 -Configuration Release`。

`tools/StudioX.CodeTemplateValidation` 接收新证据目录，验证模板持久化与范围、并发/取消/基线冲突、异常文件保留、解析与上限、选区转义、UTF-16 光标、CRLF/LF 和缩进。

Desktop `--preview-code-templates <新目录>` 使用隔离用户数据和文本工程，验证实际 AvalonEdit 撤销/重做、未保存行为、过期/只读目标、无 clangd 补全和入口，并渲染管理、编辑、变量窗口的深浅主题。不启动语言工具或连接硬件。

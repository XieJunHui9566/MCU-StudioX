# 源码登记与编译列表

工程树中的文件存在与参与编译分别核对。新增、粘贴、移除或改名源码后，通过“工具 → 工程与构建 → 源码登记与编译列表…”打开登记窗口；工程树、源码右键与命令面板也有相同入口。IDE、插件、AI 和外部文件操作共用工程同步通知，名称变化会把菜单标记为“文件变化待核对”，不会直接改写构建文件。

1. 选择用户 `CMakeLists.txt`。默认定位到选中文件最近的构建文件；ESP-IDF 选择组件构建文件。多目标文件必须明确选择目标。
2. 核对新增、缺失和已观察到的改名建议；Ctrl / Shift 多选。列表也允许移除仍存在的源码登记，磁盘文件会保留。
3. 点击“预览修改”，比较修改前后。预览包含当前未保存的 CMake 内容；选择或目标变化会使上次预览失效。
4. 点击“应用到编辑器”，修改进入未保存缓冲区。Ctrl+Z 可一次撤销；“撤销上次源码登记”保留登记前已有的未保存内容。后续编辑使整体撤销失效时，需要先核对后续修改。
5. 保存构建文件后重新配置或编译，查看本次原始构建日志和 `compile_commands.json`。窗口自身不执行构建；改名后仍需核对 `#include` 与其它引用。

## 支持范围

| 构建写法 | 自动登记范围 |
| --- | --- |
| 原生 CMake | 当前文件顶层声明的 `add_executable` / `add_library` 字面量源码，以及该目标的 `target_sources(PRIVATE/PUBLIC ...)`；新增项进入明确的 PRIVATE 范围 |
| ESP-IDF | 当前组件唯一的顶层 `idf_component_register(SRCS ...)`；保留 INCLUDE_DIRS、REQUIRES、PRIV_REQUIRES 等其它参数；缺少或为空的 SRCS 可补充 |
| 路径与源码 | 项目内相对路径，C/C++ 和 `.s` / `.S` GCC 汇编；支持空格、中文、字面量分号列表及目录改名，保留其它文本、注释和换行 |

变量、生成表达式、转义、条件/循环/函数/宏内登记、命令重定义、FILE_SET、INTERFACE 源码、目标源码属性写法，以及 IDF `SRC_DIRS` / `EXCLUDE_SRCS` 会明确拒绝自动修改并保留原始诊断。目录收集按已有规则核对，不转换为逐文件列表。此功能不求值 CMake，也不推断 `include` 或其它构建文件动态生成的目标关系；用户仍需审阅目标和本次实际编译结果。源文件支持不扩展工程锁定编译器的语言能力，例如 SDCC 工程继续只使用 C。

不登记头文件、设备管理源码、SDK、managed_components、构建产物或链接路径。跨 ESP-IDF 组件移动分别在原组件移除、在新组件加入。改名依据统一通知中的旧/新路径；事件丢失时展示移除与新增，不按文件名或内容猜测身份。最多扫描 20,000 项、选择 1,000 项；预览文本显示最多 200,000 字符，计划保留完整内容。

应用前复核工程清单、磁盘哈希、编辑快照及新源码存在性。只读、工程切换、关闭原缓冲区、外部修改、后续编辑、复制误当改名、路径越界或源文件消失均拒绝应用，不覆盖其它内容。构建文件保存基线、原始工具诊断与已有用户改动继续保留。

## 维护验证

`tools/Test-SourceRegistration.ps1 -OutputDirectory <新目录> -BuildArtifactsDirectory <隔离构建目录>` 检查应用服务与真实 WPF：路径边界、语法拒绝、注释/换行、目标选择、组件归属、预览失效、未保存应用、一次撤销、保存、外部目录移动、删除与工程切换，并保留深浅主题和最小窗口截图。

修改登记解析、建议、编辑应用或编译列表联动时声明 `-MaintenanceAreas editor,sources`，传入已有 `-LanguageRuntime` 和 `-SourceRegistrationInputs <JSON>`。机器门槛要求真实原生 MCU 与 ESP-IDF 的加入 → 改名 → 移除三阶段全部配置、编译和链接成功，实际编译数据库包含当前源码且不残留旧路径，移除登记后源码仍存在。未提供输入只能形成离线基线结论，不能表述为真实编译验收。

输入 JSON 字段为 `runtime`、`packs`、`nativePackId`、`nativePackVersion`、`nativeDevice`、`nativeTemplate`、`idfPackId`、`idfPackVersion`、`idfDevice`、`idfTemplate`。路径仅放在本机验收输入，工程锁及机器清单不写开发者绝对路径。验证完整核对所选安装包后在新的隔离工程操作，不下载 SDK、不连接硬件、不修改安装中的工具或器件包。详见 [维护门槛](MAINTENANCE_GATES.md)。

语法依据：[CMake target_sources](https://cmake.org/cmake/help/latest/command/target_sources.html)、[CMake 参数与注释](https://cmake.org/cmake/help/latest/manual/cmake-language.7.html)、[ESP-IDF 构建系统](https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-guides/build-system.html)。

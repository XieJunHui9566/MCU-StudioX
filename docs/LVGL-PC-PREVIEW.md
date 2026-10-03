# LVGL PC 窗口预览

StudioX 将工程的 LVGL 8.3.x UI 源码、字体、图片和配置编译为 Windows 本机程序，在独立窗口显示实际 LVGL 软件渲染结果。
预览通过 Win32 显示与输入端口运行，不需要 SDL，不连接 MCU，不模拟 MCU 寄存器、LCD 电气时序或 RTOS 调度。
领域和应用服务不依赖 WPF；桌面、内置 Agent 和外部 MCP 使用同一预览服务。

## 内置 PC 开发环境组件

默认使用随 StudioX 分发的 Windows x64 MinGW GCC / G++ 13.1.0；有预览配置的工程可以直接启动，无需设置开发机上的编译器路径。
本机 C 和 C++ 构建均使用该开发环境组件，C++ 标准库、Windows 头文件、链接库和编译器 DLL 一同分发。
CMake 和 Ninja 复用工程锁定的现有开发环境组件，不另外复制一份。

开发环境组件身份为 `pc.mingw/1.0.0`、编译器身份为 `mingw-gcc-13.1.0`，清单 `purpose` 为 `windows-native`。
清单定义 `gcc`、`gxx`、`ar`、`ranlib`、`as`、`ld`、`objcopy`、`objdump`、`size` 九个角色，并对整棵工具目录的文件记录 SHA-256。
启动前检查身份和文件完整性；编译器、头文件、库或 DLL 缺失/被修改时，保留 `TOOLSET_MISSING` / `TOOL_HASH` 等原始诊断。
失败不会借用 PATH 上的其他 GCC，工具目录搬移后仍以当前运行目录解析依赖。

旧版机器设置中保存的外部 GCC 路径会停用并默认切换到内置链，避免继续依赖开发者目录。
高级用户可以明确选择外部 GCC，也可以恢复内置链；选择只保存到机器设置，不进入可分享的工程。
内置发行仅保留本次 C/C++ 所需组件和许可证，约 342 MiB；不打包 Fortran、GDB、文档及缓存。
构建缓存复用同一工程目录，不为每次预览保留开发环境组件或可执行文件的历史副本。

## 接入自定义 UI

在 LVGL 面板点击「配置自定义 UI」，向导分为「1 · LVGL 库」「2 · UI 与入口」「3 · 审阅与保存」。

1. 点击「扫描 LVGL 库」，选择实际候选。扫描从文件内容核对精确版本及核心文件，可穿过多层外包装目录；库不必叫 `lvgl`。多份库均列出，不按名字或日期自动选取；8.3.x 以外的版本明确标记不支持。
2. 选择工程内的 UI 目录，点击「分析 UI、配置与入口」。向导列出 C/C++ 页面、字体/图片源码、头文件目录、`lv_conf` 配置头和入口。`main`、MCU/HAL/RTOS 依赖文件显示诊断并默认不选；需要的 PC 适配源可以明确选择。
3. 审阅入口、配置头、源码、include、核心源码排除项及运行时资源目录，点击「检查配置与依赖」。库核心自动编译，不能再把相同核心文件加入 UI；来自另一份 LVGL 的源或 include 会拒绝，避免混用版本。
4. 点击「保存配置」只保存 JSON；「保存并启动预览」随后通过正常构建启动窗口。编译器完整诊断保留，编译失败继续显示上次成功窗口并标记旧版本。

检查是基于源码文本的辅助诊断，不替代 C/C++ 编译和链接。宏生成的入口、复杂条件编译和自定义文件系统需要显式审阅。
向导复用用户选择的源码与资源，不生成空白 UI 来替代缺少的页面。

编译到 C 数组的图片、字体加入 `sourceFiles`；从文件系统加载的图片、字体等目录加入 `resourceDirectories`。
运行时资源必须在工程内，按照工程相对结构复制到当前预览工作目录。例如选择 `ui/assets` 后，代码仍以 `ui/assets/icon.bin` 查找文件。
每次成功重建使用独立的当前工作目录；失败不会替换运行窗口的资源。没有选择的目录不会被自动复制。
使用 LVGL 文件路径加载资源时，需要在 PC 配置里启用匹配的文件系统驱动和解码器，例如 Win32 文件驱动；保存资源映射本身不会自动打开全部解码功能。

## 工程配置

在工程中保存 `.studiox/lvgl-preview.json`，格式版本为 1。
库目录允许指向共享 LVGL，UI 与资源文件由目标构建和 PC 构建复用；PC 入口需要剥离 GPIO、启动代码、串口及 MCU 中断。
PC 编译器由内置开发环境组件解析；明确选择的外部 GCC 路径由机器设置保存，不能写入可分享的工程配置。

```json
{
  "formatVersion": 1,
  "lvglDirectory": "../../../LVGL_v8.3",
  "configurationHeader": "include/lv_conf_pc.h",
  "sourceFiles": ["src/ui.c", "assets/background.c"],
  "includeDirectories": ["include"],
  "uiDirectory": "src",
  "resourceDirectories": ["assets/runtime"],
  "entryPoint": "studiox_ui_start",
  "width": 240,
  "height": 320,
  "drawBufferRows": 10,
  "drawBufferCount": 1,
  "zoom": 2,
  "colorDepth": 16,
  "autoRebuild": true,
  "targetFramesPerSecond": 30,
  "displayBandwidthBytesPerSecond": null,
  "displayBandwidthSource": null
}
```

`entryPoint` 是 `void studiox_ui_start(void)` 形式的 C 函数；C++ 入口使用 `extern "C"` 导出相同函数。它在 LVGL 和显示/输入驱动就绪后创建控件，不包含主循环。
需要替换公共库里的字体源等文件时，可用 `sourceExclusions` 列出相对 LVGL 根目录的排除项，并将工程内替代源加入 `sourceFiles`；不修改公共库。
PC 配置需要保留与目标一致的颜色深度、字体、复杂绘制和缓存选项。
宿主负责 tick、帧缓冲和鼠标/键盘端口；默认 `LV_TICK_CUSTOM=0` 时按真实 Windows 单调时间推进 tick，使用自定义 tick 时可调用宿主提供的 `lv_port_millis()`。
目标的 GPIO、ADC、LCD 总线与启动文件不加入 PC 的 `sourceFiles`。
窗口缩放不会改变 LVGL 的逻辑分辨率，输入坐标使用未缩放的像素。
支持 `#include "lvgl.h"` 和常见导出 UI 的 `#include "lvgl/lvgl.h"`，后者通过明确转发头绑定所选库，不靠机器上同名目录碰巧命中。
跨盘外部库可以先发现并核对版本；配置仍要求可移植的工程相对路径，需明确导入同盘后使用。

运行预览会执行工程本机代码，进程隔离不是权限沙箱。
启动前应保存编辑器中的改动。自动重建使用已启动预览的工程；编译错误保留原始诊断和上次成功画面，并将其标记为旧版本。
构建输出与日志留在工程的预览构建目录，复用共享库，不保存每次构建的历史副本。

## 资源报告的来源

| 显示项目 | 来源 | 口径 |
|---|---|---|
| 目标 Flash / RAM | 上次成功目标 ELF + MAP | 真实目标构建统计，含预留堆池、缓冲及链接器栈预留 |
| 绘制缓冲 | 分辨率、颜色深度、缓冲行数和个数 | 配置推导；确认目标采用相同配置后才能用于目标预算 |
| LVGL 当前堆、余量、最大连续块、碎片、观测峰值 | PC LVGL 运行 | 宿主 ABI 的运行统计，不能直接等同 MCU 的动态堆 |
| PC 展示帧缓冲 | 宿主窗口 | 单独列出，不算进目标 SRAM |
| FPS | PC 预览运行 | PC 软件渲染和展示成绩，不能按主频比例换算为 MCU FPS |
| 显示传输模型 | 分辨率、像素格式、目标帧率及可选实测带宽 | 像素净荷模型，不包含渲染、控制命令、总线空隙 |

RGB565 的绘制缓冲字节数为 `宽 × 缓冲行数 × 2 × 缓冲个数`。
240×320、单个 10 行缓冲为 4,800 字节；双缓冲为 9,600 字节；单个全屏缓冲为 153,600 字节。
宿主需要的 32 位展示面在相同分辨率下为 307,200 字节，不属于 MCU 的绘制缓冲。

显示净荷为 `宽 × 高 × 每像素字节 × 目标 FPS`；240×320 RGB565 全屏 30 FPS 为 4,608,000 字节/秒。
配置了 `displayBandwidthBytesPerSecond` 后，模型给出 `带宽 / 单帧像素字节数` 的传输上限，并保留 `displayBandwidthSource` 来源。
例如当前 CH32V307 GPIO LCD 端口先前测得约 233,025 字节/秒，对应完整帧传输上限约 1.517 FPS。
这是此前端口标定的模型值，不是当前 PC 的 FPS，也不等同 MCU 的完整渲染速度；没有标定带宽时，上限保持未知。

静态 RAM 中已经包括固定 LVGL 池时，池内动态占用只表示预留空间使用程度，不能重复相加。
Flash 统计使用目标 ELF 加载地址和 MAP 存储区，PC 可执行文件大小与 PC 进程内存均不能代替它。
目标构建失败或源文件变化时，保留构建来源与时间，不将以前的数字称为当前构建。
自定义 UI 的目标占用还要求当前源码凭据、目标编译数据库中的所选 UI/资源源码，以及 ELF 指纹一致。
报告的 `TargetEvidence` 区分尚未目标编译、UI 未加入固件、来源已变化及无法核对；不能核对时目标 Flash/RAM 保持未知，PC 可执行文件不会填补该数值。
共享外部库当前只有修改时间核对，`SourcesIncluded` 表示源码与构建证据匹配，不证明每个函数都被链接保留或在 MCU 上执行。

LVGL 8.3.11 的内置 `lv_mem_monitor.max_used` 没有涵盖所有 realloc 增长。
周期读取已用空间并维护最大值仅得到观测峰值，可能漏掉短时图层和 realloc 新旧块共存的瞬时峰值。
64 位 PC 的指针、size_t、结构体对齐及 TLSF 元数据与 32 位 MCU 不同；报告明确显示宿主指针宽度。
项目使用自定义 LVGL 分配器时，内置监测可能不可用；`HeapAvailable=false` 表示未知，不能把返回的 0 当成零占用。
目标栈水位、IRQ 嵌套、RTOS 各任务栈，以及 MCU 实际渲染/总线性能需要独立目标测量。

## MCP

内置 Agent 和外部 stdio 客户端均可发现以下工具，均绑定当前工程：

| 工具 | 操作 |
|---|---|
| `lvgl_project_discover` | 只读扫描库候选的版本、完整度及重复内容；外部根需要先明确授权 |
| `lvgl_ui_inspect` | 只读列出选中 UI 的源码、配置头、入口、资源目录及依赖诊断 |
| `lvgl_preview_configure` | 校验并通过 FileWrite 授权保存配置；要求保存编辑器，不编译或运行代码 |
| `lvgl_preview_start` | 通过现有 Build 授权，编译并运行 PC 窗口；要求保存编辑器 |
| `lvgl_preview_stop` | 停止该工程的预览进程 |
| `lvgl_preview_status` | 读取运行状态、原始诊断、旧版本标记和 PC 统计 |
| `lvgl_preview_screenshot` | 返回实际预览帧缓冲 PNG 图像和尺寸，不捕获桌面或其他窗口 |
| `lvgl_preview_input` | 通过预览 IPC 输入 pointer、wheel 或 key，仅作用于该预览 |
| `lvgl_resource_report` | 返回目标构建统计及 PC 资源，保留两者的来源和限制 |

`lvgl_preview_input` 示例：

```json
{"type":"pointer","x":120,"y":160,"pressed":true}
{"type":"pointer","x":120,"y":160,"pressed":false}
{"type":"wheel","delta":-1}
{"type":"key","key":10,"pressed":true}
{"type":"key","key":10,"pressed":false}
```

键值为 LVGL 键值：ENTER 10、ESC 27、NEXT 9、PREV 11、LEFT 20、RIGHT 19、UP 17、DOWN 18；也可发送 ASCII 键值。
截图以 MCP image block 返回，支持视觉模型直接检查；仅有文本能力的模型可以读取统计和日志。
这组工具不包含烧录授权，也不调用全局操作系统输入。
每个工程同一时刻只允许一个预览服务持有 `.build/pc-preview/session.lock`。
内置 Agent 使用 IDE 的服务，可控制该 IDE 打开的预览；外部 stdio 客户端可启动自己的会话，但第一版不能跨进程接管已经由 IDE 打开的窗口。
重复所有者收到 `LVGL_PREVIEW_IN_USE`，不会覆盖运行中的构建文件。

无配置时，Agent 先发现库、检查 UI，再将明确选中的 `LvglPreviewConfiguration` 作为 `configurationJson` 交给配置工具。
工程内扫描不请求写授权。工程外 discovery 须先调用 `external_project_open`，再传 `externalRootId` 和授权根内相对目录；不会扫描未经授权的绝对目录。
新的共享库读取要求 ExternalRead；已经明确选中的配置库或本会话授权范围可复用。UI 目录始终限于当前工程。

## 参考依据

- [LVGL 8.3 显示驱动、分块缓冲与 flush 同步](https://lvgl.io/docs/open/8.3/porting/display)
- [LVGL 8.3.11 内存实现](https://raw.githubusercontent.com/lvgl/lvgl/v8.3.11/src/misc/lv_mem.c)
- [GCC 每函数栈统计与调用图](https://gcc.gnu.org/onlinedocs/gcc/Developer-Options.html)

## 软件验收

`tools/StudioX.LvglValidation` 使用真实进程、IPC 和 MCP 握手验证发现、权限拒绝、未保存文件保护、状态、PNG、输入边界、资源来源及停止清理。
内置链验收额外在只含 Windows System32 的 PATH、无外部编译环境、无保存编译器设置的条件下，通过真实 MCP 编译并运行包含 C++ STL 的独立 LVGL UI。
仅对独立物理复制的 PC 开发环境组件验证搬移和编译器/头文件/标准库篡改拒绝，不修改发行工具目录或开发机原始 MinGW。
验收也覆盖旧机器配置迁移、明确选择外部链与恢复内置链；临时开发环境组件复制通过后删除，仅保留结果、截图及日志。

```powershell
dotnet build tools/StudioX.LvglValidation/StudioX.LvglValidation.csproj --no-restore
tools/StudioX.LvglValidation/bin/Debug/net10.0/StudioX.LvglValidation.exe <LVGL工程目录> <StudioX运行时目录> --bundled <StudioX.Cli.exe>
tools/StudioX.LvglValidation/bin/Debug/net10.0/StudioX.LvglValidation.exe <已有LVGL工程目录> <StudioX运行时目录> --custom-ui <StudioX.Cli.exe>
```

内置链证据保存在 `artifacts/validation/lvgl-bundled-current`。验收没有连接、下载或调试硬件。
PC 验证结果不能作为实板 FPS、堆或屏幕显示证据。

自定义 UI 另用 `--custom-ui` 模式，在 `artifacts/validation/lvgl-custom-ui-current` 保留发现报告、诊断、配置与截图。
少量库标记 fixture 验证扫描边界与版本诊断；真实窗口复用共享 LVGL，包含 C/C++ 两个页面、独立图片源和运行时资源文件，通过实际输入及像素变化核对效果。
此模式不重复复制 LVGL 或开发环境组件，也不进行 MCU 编译或硬件操作。
已有工程的目标资源只读回归可用 `--legacy-resources`，保留现有构建来源和选中源码的编译数据库对应关系，不重新编译固件。

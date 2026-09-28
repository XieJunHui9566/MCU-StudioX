# Raspberry Pi MicroPython 工程

RP2040、RP2350 继续独立分包：`raspberrypi.rp2040/0.2.0` 与 `raspberrypi.rp2350/0.2.0`。每包保留原有三种 Pico SDK C 模板，另加 **MicroPython · 最小脚本** 和 **MicroPython · LED 闪灯**。产品版本不随器件包版本变化。

| 包目标 | MicroPython 板型 | 解释器版本 | 配置范围 |
| --- | --- | --- | --- |
| RP2040-PICO | RPI_PICO | 1.29.0 | 官方 Pico / Pico H，2 MiB Flash、264 KiB SRAM |
| RP2350A-PICO2 | RPI_PICO2 | 1.29.0 | 官方 Pico 2，ARM 固件，4 MiB Flash、520 KiB SRAM |

板上 Flash 中的解释器与文件系统会占用空间，表中物理容量不等于 Python 堆或可用文件空间。Pico W、Pico 2 W、RP2350 RISC-V 和任意第三方板型不由本配置推断支持。兼容板须确认固件、外部 Flash 与 LED 接线。

## 创建与编辑

新建工程选择「树莓派 · Raspberry Pi」→ 对应 RP2040 / RP2350 包与板型 → MicroPython 模板。工程包含根目录 `main.py`、`boot.py`、`README.md`、`.studiox/project.json` 和 `device/manifest.json` 身份记录；不复制 C SDK，不生成 CMake、ELF 或本机编译器锁。

工程类型明确记录为 `MicroPython`，解释器板型和版本来自所选模板。打开时直接进入 Python 编辑器，保留高亮、当前文件符号补全、参数提示和 `#` 注释。此模式另提供 `machine`、`time`、`rp2`、`micropython`、`gc` 等常用 API，以及显式导入、别名和直接构造对象的成员提示，例如：

```python
from machine import Pin
led = Pin("LED", Pin.OUT)
led.toggle()
```

提示以 1.29.0 RP2 API 为基础，尚不做完整作用域、跨文件或第三方库推断；不会把 CPython 专有调用参数加入 MicroPython 提示。板级提示只在明确的 MicroPython 工程中启用。

编辑器右键提供「转到定义」「转到声明」「查找引用」，快捷键分别为 `F12`、`Ctrl+F12`、`Shift+F12`。普通 Python 定义和显式导入可在工程内跳转；`Pin`、`sleep_ms` 等固定 API 跳到只读接口声明。`Ctrl+F` / `Ctrl+H` 提供查找和替换，当前文件全部替换可一次撤销。静态解析范围见 [Python 编辑说明](PYTHON_EDITOR.md)。

## 解释器、REPL 与脚本上传

首次使用，在官方 [Pico 固件页](https://micropython.org/download/RPI_PICO/)或 [Pico 2 固件页](https://micropython.org/download/RPI_PICO2/)选择 **v1.29.0**，按 BOOTSEL 说明安装。需要保留的原固件与文件应先由用户备份。器件包记录准确官方固件 URL，但不分发或自动刷写 UF2，也不下载 SDK。

打开 MicroPython 工程后，点击顶部「下载」进入「MicroPython 下载 / REPL」页面；「下载设置」也可打开该页面。操作入口已从工具菜单移除。

1. 刷新并选择 USB COM 端口，也可手动输入。选择的端口始终显示在输入框中，刷新保留选择。点击「连接 REPL」后，中断当前脚本并进入 raw REPL，核对解释器报告的板名及 1.29.0 版本。识别信息来自固件，不能当作芯片身份或外部 Flash 的独立硬件验收。
2. 在 REPL 输入多行片段并执行，标准输出和异常输出保持原文。表达式请用 `print`；单次限制 16 KiB / 30 秒，输出最多 1 MiB。「停止 / 断开」取消操作、尝试 Ctrl-C 中断并返回普通 REPL，然后释放端口。
3. 填写 `main.py`、`boot.py` 或 `lib/sensor.py` 等相对路径，点击顶部「下载」或页面中的「下载脚本」。两者执行相同流程：保存工程中已打开的编辑文件，读取所选脚本，在需要时连接明确选择的端口，然后备份、下载和校验。未选择端口时只显示提示，不自动连接设备。单文件最多 256 KiB。输入文本统一编码为 UTF-8，上传过程中使用固定快照。
4. 点击顶部下载旁或页面中的「开始运行」，执行所填路径的板上已下载脚本，默认 `main.py`。尚未连接时使用明确选定的端口连接并核对身份。此操作不上传本地修改，编辑后请先下载。启动握手限 10 秒，主程序没有 30 秒执行期限；标准输出与异常持续显示，正常结束后可再次运行或使用 REPL。运行中禁用重复启动、下载和 REPL 执行，顶部「停止」可中断并断开连接。

顶部「停止」与页面「停止 / 断开」均可取消 MicroPython 操作并释放连接。窗口较窄时，REPL 输入和日志上下排列，页面可滚动；长路径日志自动换行。

同名文件先完整读回并核对 SHA-256，备份到本工程 `.studiox/micropython-backups/<宿主 UTC 时间与运行 ID>/`，旁边保存原路径、哈希和板型记录。新内容先写入板上同目录临时文件，分块传输并校验，再重新核对旧内容后执行替换，最后再次校验目标。没有旧文件时也会检查上传期间目标是否被创建。

上传不会自动运行脚本、同步删除板上其他文件或写入解释器映像。「开始运行」将指定文件作为 `__main__` 执行并保留文件名用于异常定位，不复位解释器；需要脱机启动时可在下载 `main.py` 后复位板卡。停止使用 Ctrl-C；程序若屏蔽该中断，主机断开连接不能保证板上代码已停止。默认 `boot.py` 保留 USB REPL，不主动配置外设。

持续运行的输出按 UTF-8 增量解码，不受 REPL 片段的总输出限制。界面每 100 毫秒取出有界显示缓存，超过 128 KiB 的较早显示会被截断并提示；串口原始接收帧丢失仍按协议错误中止，不把丢帧当作完整输出。程序输出保持原文，状态日志的时间戳来自宿主。

传输失败、设备断开或取消会结束当前连接；原始异常保留在输出中。替换前失败保留旧文件；替换后校验失败需利用备份恢复。中断时可能留下名称含 `.studiox-` 的临时文件，传输日志记录其路径；不会自动清理板上不明文件。备份范围仅为此次同名脚本，不是整块 Flash。

串口使用 `DeviceHub` 的单一所有者机制，与串口终端、绘图和其他下载流程互斥。关闭 MicroPython 标签、切换/关闭工程或退出 IDE 会取消在途操作并释放会话。显示时间戳均来自宿主。此工程的 GCC 编译、OpenOCD 下载和 GDB 调试入口禁用，应用后端也拒绝这些操作。

## 包制作与验证

```powershell
& '<本机 Python>' tools/New-RaspberryPiMicroPythonPacks.py `
  --rp2040 artifacts/packs/RP2040-0.1.0/raspberrypi.rp2040-0.1.0.mcupack `
  --rp2350 artifacts/packs/RP2350-0.1.0-verified/raspberrypi.rp2350-0.1.0.mcupack `
  --cli src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll --output '<新目录>'
dotnet run --project tools/StudioX.MicroPythonValidation -- '<包输出根目录>' '<本机 Python>' '<新验收目录>'
./tools/Build.ps1
& './src/StudioX.Desktop/bin/Debug/net10.0-windows/MCU StudioX.exe' --preview-micropython '<新预览目录>' '<RP2040 包>' '<RP2350 包>'
```

交付位于 `artifacts/packs/RaspberryPi-MicroPython-0.2.0/`，每个子目录的 `index.json` 记录准确归档 SHA-256。打包器核验原 C 包逐文件哈希，保留 C SDK 源码及许可证；新增模板来源记录于 `micropython/provenance.json`。开发版内置目录和后续发行脚本使用两个独立 0.2.0 包；已有 0.1.0 工程与包记录保持原样。

离线验收使用模拟传输与隔离 CPython 文件系统夹具，覆盖分片 UTF-8、REPL 确认/错误帧、端口互斥、原脚本备份、分块上传、摘要不匹配、取消、路径边界与错误板型拒绝。它验证宿主协议和传输脚本，不是 MicroPython 解释器或真实 USB 验收。桌面预览另检查模板选择、工程生成、实际 API 补全、深浅主题和入口隔离。本轮未连接开发板、刷写解释器或上传真实设备文件。

2026-09-28 验证记录：

新增「开始运行」：顶部下载旁与脚本页面均可启动板上脚本，实时输出，停止后释放串口。`artifacts/validation/micropython-run-build.txt` 为 0 警告、0 错误；离线协议 / 文件系统检查 101 项通过，结果在 `artifacts/validation/micropython-run-20260928-3/result.txt`，其中 RP2040 模拟会话持续超过 30 秒，两系列均覆盖累计输出超过 1 MiB、中文分片、真实异常路径和停止后重连。实际 WPF 的按钮路由、端口必选、显示缓存有界、深浅主题及窄窗口检查通过，截图与结果位于 `artifacts/validation/micropython-run-preview/`。未操作真实硬件。

后续界面与编辑修正：下载改接顶部按钮并同步停止入口，端口选择和手动输入显示修复，页面支持窄窗口滚动；新增静态跳转、引用列表及查找 / 单个替换 / 当前文件全部替换。隔离构建 `artifacts/validation/micropython-editor-final-build.txt` 为 0 警告、0 错误；27 项导航 / 文本匹配检查与 57 项桌面职责检查通过。两系列实际 WPF 验证覆盖 API 跳转返回、引用列表、批量替换单步撤销、非法正则保护、深浅主题、端口显示与窗口缩放，截图位于 `artifacts/validation/micropython-editor-fix-preview-final/`。本次修正没有操作真实串口。

- `tools/Build.ps1`：0 警告、0 错误；日志 `artifacts/validation/micropython-build-final.txt`。
- MicroPython 离线检查 80 项通过；结果 `artifacts/validation/micropython-20260928-final/result.txt`。通用 Python 72 项回归、领域架构 13 项和桌面架构 57 项检查通过。
- RP2040 / RP2350 桌面预览通过；结果与截图位于 `artifacts/validation/micropython-ui-20260928-final/`。
- 新包各自的 C 最小工程真实编译成功；日志 `artifacts/validation/micropython-c-rp2040.txt` 与 `micropython-c-rp2350.txt`。旧包原有源码、模板及许可证逐文件保持一致。
- 本机包仓库与开发版内置索引已更新为两个 0.2.0 包；发行脚本的树莓派资源复制段独立校验通过，未制作安装包或更改产品版本。
- 全局 MCP 契约检查未通过：现有实现比基线增加 6 个 AG32 接口，另有 1 个 AG32 接口描述不同；差异仅涉及既有 AG32 工作，未改写基线。明细 `artifacts/validation/micropython-mcp-baseline-diff.json`。

协议与 API 来源：[RP2 1.29.0 快速参考](https://docs.micropython.org/en/v1.29.0/rp2/quickref.html)、[raw REPL 协议](https://docs.micropython.org/en/v1.29.0/reference/repl.html)、[machine 模块](https://docs.micropython.org/en/v1.29.0/library/machine.html)、[rp2 模块](https://docs.micropython.org/en/v1.29.0/library/rp2.html)。

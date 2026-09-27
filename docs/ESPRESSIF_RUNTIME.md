# Espressif 共享 SDK 准备与语言分析

用户入口、下载与 MCP 范围见 [Espressif 原生 SDK 支持](ESPRESSIF_SUPPORT.md)。本页记录开发资源、精确版本和语言分析约束。

## 支持范围

| 器件包条目 | SDK 构建目标 | SDK / 工具集 |
| --- | --- | --- |
| ESP32-WROOM-32 | `esp32` | ESP-IDF 5.5.4 / `espressif.idf/5.5.4` |
| ESP32-P4 | `esp32p4` | 同上 |
| ESP32-S3 | `esp32s3` | 同上 |
| ESP32-C3 | `esp32c3` | 同上 |
| ESP32-C5 | `esp32c5` | 同上 |
| ESP32-C6 | `esp32c6` | 同上 |
| ESP8266 | `esp8266` | ESP8266 RTOS SDK v3.4 / `espressif.esp8266-rtos/3.4.0` |

ESP8266 不属于 ESP-IDF 5.5.4 支持目标，使用独立 SDK 和 LX106 编译器。WROOM-32 按官方数据手册 v3.8 声明 4 MiB Flash、520 KiB SRAM，并生成 4 MiB Flash 默认配置。其他条目是 SDK 目标，不猜测模块后缀、PSRAM 或板级 Flash；包中容量 `0` 表示未声明，用户须按实物在 SDK 配置中核对。应用可用 RAM 与 Flash 分区以原生构建结果为准。

ESP32 六个 0.1.1 器件包使用 IDF 自带的 `get-started/hello_world` 与 `system/freertos/real_time_stats`。后者的官方 README 明确支持全部六个目标；仅列 C3/S3 的 `basic_freertos_smp_usage` 不用于通用模板。原始 `main/` 组件、源码名、依赖、`MINIMAL_BUILD` 与 FreeRTOS 运行统计配置保留；工程清单通过 `entryFile` 记录原始入口路径。官方源码按 v5.5.4 的 commit 与逐文件 SHA-256 核验，保留许可证和版权头。既有 ESP8266 0.1.0 包逐字节复用，本轮不修改其模板。

## 语言分析范围

代码提示按每个源文件导入 SDK 实际宏、头文件路径和语言标准，8.3 路径别名转换为编辑器的规范路径。首次打开未配置工程先提供通用提示，提示配置或编译以获得 SDK API。新增 C/C++ 文件继承相邻应用文件的 SDK 分析环境。Xtensa 使用通用 32 位 C/C++ 声明解析；RISC-V 使用标准 32 位目标并转换厂商扩展参数。语言服务缓存不修改原始 `compile_commands.json`，解析结果不替代真实 SDK 的 ABI 和编译验证。

原生工具运行目录必须能够表示为不含空格和非 ASCII 字符的 Windows 短路径；无法取得有效短名称时明确拒绝，避免配置成功但后续编译器处理路径失败。编辑器仍使用工程的原始规范路径。应用组件、图片、字体和预编译库应放在工程内；构建检查原生 CMake/Ninja 输入，工程外的未锁定输入不会获得下载凭据。

## 开发者准备

普通用户使用完整发行目录。下列命令供开发者从已准备的完整 SDK 和工具构建共享资源，SDK 不复制到每个工程：

```powershell
python tools/Prepare-EspressifRuntime.py `
  --idf-root '<ESP-IDF 5.5.4 根目录>' `
  --tools-root '<Espressif tools 根目录>' `
  --python-env '<Espressif Python 3.11.2 环境>' `
  --git-root '<完整 Git 根目录>' `
  --output artifacts/tool-runtime/toolsets/espressif.idf/5.5.4

python tools/Prepare-Esp8266Runtime.py `
  --sdk-root '<ESP8266 RTOS SDK v3.4 根目录>' `
  --downloads '<已核验厂商工具归档目录>' `
  --idf-runtime artifacts/tool-runtime/toolsets/espressif.idf/5.5.4 `
  --output artifacts/tool-runtime/toolsets/espressif.esp8266-rtos/3.4.0

dotnet build src/StudioX.Cli/StudioX.Cli.csproj
python tools/New-EspressifPacks.py `
  --idf-root '<完整 ESP-IDF 5.5.4 根目录，包含官方 examples>' `
  --esp8266-pack '<未改变的 espressif.esp8266-0.1.0.mcupack>' `
  --output '<新的小包生成目录>'
```

ESP8266 准备脚本在旧 Kconfig 前端归档缺失时会下载厂商固定版本归档；其余归档必须存在且匹配固定 SHA-256。准备过程中核对工具入口，保留 SDK、工具和 Python 包许可证并生成完整哈希清单。工具版本和官方来源见 [Espressif 许可声明](../licenses/Espressif-NOTICE.md)。

ESP8266 v3.4 的原生组件清单中，pthread 条件变量的强制链接符号拼写与实际源码导出不同。生成工程显式纳入 SDK 工具组件和 pthread，并在应用链接选项中引用真实的 `pthread_include_pthread_cond_var_impl`；共享 SDK 源码和工具哈希保持不变。

发行小包使用 `artifacts/packs/Espressif-0.1.1/` 中七个 `.mcupack` 和 `index.json`，其中 ESP32 六包版本为 0.1.1，ESP8266 为未改变的 0.1.0；生成时的 `source/` 仅为工作目录，不复制到发行包。开发 bundle 位于 `artifacts/device-packs-development/`，用于本地构建时自动导入这七个包，不用于替代完整厂商发行索引。SDK 工具集由 Desktop 的运行资源规则共享；`Publish.ps1` 在发布前独立验证全部 SDK 文件哈希，以及包路径、版本、设备身份与来源摘要。新版模板使用独立的包版本，已安装旧包和用户既有工程不会被同版本覆盖。

## 离线验证

```powershell
dotnet run --project tools/StudioX.EspressifValidation -- --official `
  artifacts/packs/Espressif-0.1.1 '<新的验证输出目录>'

dotnet run --project tools/StudioX.EspressifValidation -- --bundle-upgrade `
  artifacts/packs/Espressif-0.1.0 artifacts/packs/Espressif-0.1.1 '<新的升级验证输出目录>'

dotnet run --project tools/StudioX.EspressifValidation -- --language `
  artifacts/tool-runtime '<已原生配置的 ESP 验证工程目录>' '<新的语言验证输出目录>'
```

官方模板验证覆盖六个 ESP32 目标的十二个工程，包含原生源码、组件依赖、配置与许可证保持一致，以及 SDK/目标/工具身份边界、未声明容量和无开发者工具路径。先前七个包、十四个工程的基础验证包含独立 ESP8266 模板。升级验证覆盖六个新版自动导入、未改变的 ESP8266 跳过、重复启动和既有工程内容保持不变。实际 C3/S3 clangd 验证已覆盖 SDK API 与 FreeRTOS 补全、只读 SDK 定义跳转、新 C++ 文件、8.3 路径映射，以及原始编译数据库保持不变；未配置 C5 工程的通用提示与状态说明也通过。

## 厂商资料

- [ESP-IDF 5.5.4 支持目标](https://github.com/espressif/esp-idf/blob/v5.5.4/tools/idf_py_actions/constants.py)
- [ESP-IDF 5.5.4 工具版本](https://github.com/espressif/esp-idf/blob/v5.5.4/tools/tools.json)
- [ESP32-WROOM-32 数据手册](https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32_datasheet_en.pdf)
- [ESP8266 RTOS SDK v3.4](https://github.com/espressif/ESP8266_RTOS_SDK/tree/v3.4)

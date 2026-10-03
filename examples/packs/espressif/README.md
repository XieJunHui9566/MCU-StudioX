# Espressif 原生 SDK 器件包

## 多版本 ESP-IDF 模板

原有 5.5.4 / 0.1.1 包继续保留。在线器件包目录补充以下精确组合，每组均含六个独立目标包及 Hello World、FreeRTOS 两种官方模板：

| IDF SDK / 开发环境组件 | 器件包版本 | 上游标签 |
| --- | --- | --- |
| 5.5.5 | 0.2.0 | v5.5.5 |
| 6.0.3 | 0.3.0 | v6.0.3 |
| 6.1.0 | 0.4.0 | v6.1 |

`idf-template-sources-<SDK>.json` 固定各版本的上游提交、原始与 LF 规范化 SHA-256、目标支持文件及公开组件指纹。新包不复用其他 SDK 的示例，也不改变已有包或工程。使用对应官方源码和已构建 CLI 可重建，例如：

```powershell
python tools/New-EspressifPacks.py --output <新输出目录> --cli <StudioX.Cli.dll> --idf-root <官方 IDF 6.1 源码目录> --template-sources examples/packs/espressif/idf-template-sources-6.1.0.json --pack-version 0.4.0 --component-version 6.1.0 --idf-only
```

维护验证入口为 `tools/StudioX.EspressifPackSetValidation`：`inspect` 检查四 SDK 的 24 包导入、两模板生成和明确版本选择；指定 SDK 则使用已有隔离运行时编译该版本六目标的全部模板。验收不连接或烧录硬件。

`tools/New-EspressifPacks.py` 生成七个独立 StudioX 格式 1 包：ESP32-WROOM-32、ESP32-P4、ESP32-S3、ESP32-C3、ESP32-C5、ESP32-C6 和 ESP8266。

ESP32 目标锁定 ESP-IDF **5.5.4**；ESP8266 锁定独立 **ESP8266 RTOS SDK v3.4**（工程版本写为 `3.4.0`）。ESP8266 不属于 ESP-IDF 5.5.4 的支持目标。包只包含模板、目标元数据和来源记录，完整 SDK 与工具链使用 IDE 的共享运行时，不复制到每个工程。

WROOM-32 模块的物理资源按官方数据手册 v3.8 声明为 4 MiB SPI Flash、520 KiB SRAM。其他条目是 SDK 构建目标，外置 Flash、PSRAM、封装和具体可用 RAM 依赖实际器件及 SDK 配置；包中容量 `0` 表示未声明，不能用于推断下载范围或可用堆。Flash 分区和可用内存以 SDK 生成产物为准。

ESP32 系列的 0.1.1 包使用 ESP-IDF 5.5.4 原始 `get-started/hello_world` 和 `system/freertos/real_time_stats` 示例，保留 `main/` 下原始源码文件名、组件 CMake、依赖、`MINIMAL_BUILD` 和 FreeRTOS 统计配置。该 FreeRTOS 示例的官方支持列表包含全部六个已接入目标。生成时仅修改工程名称、锁定目标和已核验的 WROOM-32 Flash 默认容量。官方许可证、源码版权头及逐文件 SHA-256 保留在包与工程中；来源固定为 v5.5.4 commit `735507283d5b2f9fb363a1901172dbd9e847945d`。

ESP8266 继续使用现有 0.1.0 包的 `src/main.c` 模板，本次不替换其模板或 SDK。开发 bundle 和完整发行索引使用六个 ESP32 0.1.1 包加一个未改变的 ESP8266 0.1.0 包；更新不会覆盖已创建的用户工程。

## 资料

- [IDF 5.5.4 官方支持目标](https://github.com/espressif/esp-idf/blob/v5.5.4/tools/idf_py_actions/constants.py)
- [IDF 5.5.4 工具锁定记录](https://github.com/espressif/esp-idf/blob/v5.5.4/tools/tools.json)
- [ESP32-WROOM-32 数据手册 v3.8](https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32_datasheet_en.pdf)
- [ESP8266 RTOS SDK v3.4](https://github.com/espressif/ESP8266_RTOS_SDK/tree/v3.4)
- [ESP8266 v3.4 工具版本](https://github.com/espressif/ESP8266_RTOS_SDK/blob/v3.4/tools/toolchain_versions.mk)

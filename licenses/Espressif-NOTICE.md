# Espressif 内置工具组件

ESP32 工程使用共享的 ESP-IDF **5.5.4**；ESP8266 工程使用独立的 ESP8266 RTOS SDK **3.4**。芯片包和工程只引用组件，不携带另一份 SDK。

## 来源与固定版本

| 组件 | 版本 | 可信来源 |
| --- | --- | --- |
| ESP-IDF | 5.5.4 | <https://github.com/espressif/esp-idf/releases/tag/v5.5.4> |
| ESP32 Xtensa / RISC-V GCC | esp-14.2.0_20260121 | <https://github.com/espressif/esp-idf/blob/v5.5.4/tools/tools.json> |
| ESP8266 RTOS SDK | 3.4 | <https://github.com/espressif/ESP8266_RTOS_SDK/releases/tag/v3.4> |
| ESP8266 Xtensa LX106 GCC | 8.4.0-esp-2020r3 | <https://github.com/espressif/ESP8266_RTOS_SDK/blob/v3.4/tools/tools.json> |
| ESP8266 Windows Kconfig frontend | 4.6.0.0-idf-20190628 | 同上官方工具清单 |
| Python | 3.11.2 / 3.8.10 | Espressif 官方 Windows Python 发行环境 / <https://www.python.org/downloads/release/python-3810/> |
| CMake / Ninja | 3.30.2 / 1.12.1 | Espressif 本机发行环境中的固定组件 |
| esptool | 4.12.dev1 / 2.8 | 已核验 Python distribution metadata；旧 SDK 原生构建附带 esptool 2.4.0 |

每个组件的 `SOURCE.json` 记录来源、版本与归档摘要，`toolset.json` 索引 SDK、编译器、Python 模块、库和许可证的逐文件 SHA-256。旧 SDK 的 GCC 与 Kconfig 归档同时核对厂商清单的 SHA-256。主机工具版本与厂商推荐版本不同的组合必须经过本软件的原生构建验收。

## 许可证

保留 SDK 根目录 `LICENSE`，以及 SDK 各组件的许可证、版权与第三方声明；具体文件中的许可优先。ESP-IDF 主要采用 Apache-2.0，ESP8266 SDK 以随发行源码的许可为准。

GCC、binutils、Newlib/Picolibc 等工具链内容保留发行目录中的 COPYING、LICENSE、NOTICE 与运行库例外说明；这些组件可能采用 GPL、LGPL 或其他独立许可证。对应源码及工具发布入口见上述官方清单及 <https://github.com/espressif/crosstool-NG>。Python 保留 `LICENSE.txt`，Python 包保留各 distribution 的许可证及 metadata。CMake、Ninja、Git 保留各自发行内容和许可证；Git 的说明另见本目录 `Git-for-Windows-NOTICE.md`。

运行时不包含开发者的 Git 历史、Python 虚拟环境绝对路径配置或 pip 控制台包装器。构建缓存、组件管理状态和生成文件放在工程内；共享 SDK 作为已索引的只读内容使用。

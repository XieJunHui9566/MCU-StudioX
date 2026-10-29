# Arduino 工具来源

StudioX 的 Arduino 支持由独立的 C# 服务调用官方工具，不将外部工具改称自研组件。

| 内容 | 固定版本 | 原始来源与许可 |
| --- | --- | --- |
| Arduino CLI | 1.5.1 | https://github.com/arduino/arduino-cli/tree/v1.5.1 ，GPL-3.0；组件保留 `CLI-LICENSE.txt` |
| Arduino AVR Boards | 1.8.8 | https://downloads.arduino.cc/packages/package_index.json 中的固定核心归档；核心 C/C++、随附库、引导程序各按原文件版权与许可证保留，不能统一覆盖许可 |
| AVR GCC | 7.3.0-atmel3.6.1-arduino7 | 官方核心索引固定依赖；保留发行归档的 GCC、binutils、avr-libc 版权、许可证与运行库例外 |
| avrdude | 8.0.0-arduino1 | 官方核心索引固定依赖；保留发行归档版权和许可证 |
| arduinoOTA | 1.3.0 | 官方核心索引固定依赖；本轮串口上传不调用 OTA，但仍保留原归档内容与许可 |
| Arduino ESP32 | 3.3.8 | https://github.com/espressif/arduino-esp32/tree/3.3.8 ，核心以 LGPL-2.1 为主，随附文件按原许可保留；固定核心 ZIP SHA-256 `8159cefaa2eea131360ac32393d741d462583297c8c1e5169ca48139fa324173` |
| ESP32 GCC / SDK / esptool | GCC 14.2.0（2601），核心 3.3.8 锁定的 SDK 与 esptool 5.2.0 | https://espressif.github.io/arduino-esp32/package_esp32_index.json ，保留 GCC / binutils / C 库、SDK、工具及所含第三方代码各自许可证；不使用原生 ESP-IDF 工具组件替代 Arduino 锁定组件 |

`tools/Prepare-ArduinoRuntime.ps1` 保留官方归档中的原文件，并生成 `sources.json`，记录 CLI 归档 SHA-256、核心来源及校验和、每项工具的固定版本和 Windows 归档来源/校验和；`toolset.json` 为实际文件建立完整 SHA-256 索引。

`tools/Prepare-ArduinoEsp32Runtime.ps1` 使用已校验的 CLI 1.5.1，并在独立目录准备官方固定版本 `esp32:esp32@3.3.8`；跳过驱动/安装脚本，不修改系统 PATH。组件同样保留核心和依赖的来源索引、许可证及逐文件 SHA-256，剔除 CLI 本机安装标识。

本轮只准备开发和验证组件，不制作或发布产品安装包。以后再分发这些工具时，需要按各组件许可保留通知并提供相应源代码材料；本通知不替代原许可证或完整源码材料。

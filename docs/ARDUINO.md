# Arduino 编程支持（Windows）

Arduino 在统一新建页作为“起始模板”选择，底层使用独立工程类型，保留 `.ino` sketch。芯片、开发板配置、框架和工具版本分别锁定；同一 ESP32 器件可以选择原生 ESP-IDF 示例或 Arduino。IDE 通过 C# 服务调用受管理的官方 Arduino CLI，不依赖全局 Arduino IDE、Node 或系统 PATH。

## 首批范围

| 开发板 | 明确目标 | 可用程序 Flash / SRAM |
| --- | --- | --- |
| UNO R3，ATmega328P | `arduino:avr:uno` | 32256 / 2048 字节 |
| 经典 Nano，ATmega328P，新引导程序 | `arduino:avr:nano:cpu=atmega328` | 30720 / 2048 字节 |
| 经典 Nano，ATmega328P，旧引导程序 | `arduino:avr:nano:cpu=atmega328old` | 30720 / 2048 字节 |
| ESP32-WROOM-32 | ESP32 Dev Module，4 MB Flash，无 PSRAM | 以官方编译报告和分区配置为准 |
| ESP32-C3 | ESP32C3 Dev Module，4 MB Flash | 同上 |
| ESP32-C5 | ESP32C5 Dev Module，4 MB Flash，无 PSRAM | 同上 |
| ESP32-C6 | ESP32C6 Dev Module，4 MB Flash | 同上 |
| ESP32-S3 | ESP32S3 Dev Module，4 MB Flash，无 PSRAM | 同上 |

AVR 容量、16 MHz 时钟和 variant 来自 Arduino AVR Boards 1.8.8 的 `boards.txt`。ESP32 板型来自固定 Arduino ESP32 3.3.8 的 `boards.txt`，明确锁定 `FlashSize=4M`、适用型号的 `PSRAM=disabled` 和 `EraseFlash=none`，其余菜单使用该固定官方配置的默认值。通用开发板配置需要与实际模块的 Flash、PSRAM、USB 和接线核对，IDE 不自动识别。

本轮不开放 ESP32-P4、UNO R4、Nano Every、ESP8266、STM32 或 RP2350 的 Arduino 模板。软件编译通过不代表实板上传、复位、串口及传感器验收，没有沿用其他 MCU 的硬件操作授权。

## 使用入口

1. “新建工程 → 器件厂商 → 器件包 → 芯片型号 → 起始模板：Arduino”，再选择同页的“Arduino 开发板配置”。即使只有一个匹配项，也需要明确选择。ATmega328P 位于“微芯科技 / ATmega328P · Arduino AVR”目录。工程名使用英文、数字、下划线，与 `.ino` 主入口及目录名保持一致。
2. “文件 → 导入 Arduino sketch 副本…”复制现有 sketch；原目录、源码、构建文件不改写。工程的外部依赖不会根据全局 Arduino 安装猜测，需要重新明确添加。
3. “工具 → 工程与构建 → Arduino 工程与库管理…”可以更改与当前芯片匹配的开发板配置，例如 UNO / Nano 新旧引导程序；保存后旧固件凭据作废，必须重新编译。同页可搜索官方库索引，选择结果或手动填写精确版本。安装后，工程同时记录传递依赖的实际版本；不同工程的库集合独立。
4. 顶部“编译”使用官方 sketch 预处理、AVR GCC 编译和链接。构建输出保留原始诊断、退出码、Flash/RAM 用量；产物和日志写入工程 `.build/arduino/<运行编号>`。
5. 顶部“下载”先保存、重新编译，再展示准确板型、固件文件大小与 SHA-256。HEX 文本文件大小与程序 Flash 占用分别标明。下拉框列举 COM 端口，支持刷新、手动填写和恢复本工程上次明确选择；首次使用不自动选择，刷新也不改选消失的端口。确认后才预约所选端口并启动 CLI 上传及校验；端口不存在或串口监视占用时明确拒绝。取消不会打开端口。

上传阶段显示独立进度与最终结论，原始工具输出只展示一次，保存到 `.build/arduino-upload/<会话>/upload.log`，取消或异常也保留已有诊断。上传使用明确选择的 COM 端口、`--verify` 和详细输出；AVR 还需出现实际 Flash 校验记录，ESP32 需出现镜像摘要校验记录。只有 `New upload port` 或退出码 0 不足以显示“已校验”。

独立“新建 Arduino 工程”菜单和新建页按钮已移除；导入 sketch 仍保留为复制导入操作。切换到 Arduino 隐藏 ESP-IDF 版本选择，切回原生模板清除 Arduino 配置；更换芯片清除旧选择，刷新目录保留有效的明确选择。

AVR 不调用 `burn-bootloader` 或修改熔丝。ESP32 按官方 recipe 写入本次构建配套的 bootloader、分区表和主 BIN，确认窗口明确说明；关闭整片擦除选项。IDE 不自动安装 USB 驱动，驱动按实际 USB 芯片确认。

外部 `sketch.yaml` 导入时保留为 `.studiox/import/sketch.yaml`，不会让其默认 profile 隐式切换核心或下载依赖；直接在工程根添加该文件时，CLI 操作会明确拒绝，需先保存到上述记录目录。

## 工具准备与来源

开发机通过 `tools/Prepare-ArduinoRuntime.ps1` 准备 `runtime/toolsets/arduino.avr/1.8.8`，也可以传入隔离 `-RuntimeDirectory` 和 `-WorkingDirectory`。脚本只下载固定版本的官方组件，跳过驱动安装脚本；不制作安装包、不改产品版本或全局环境。

- Arduino CLI **1.5.1**：[官方发行](https://github.com/arduino/arduino-cli/releases/tag/v1.5.1)。Windows x64 ZIP SHA-256：`FABE42E0EB04D00E776A66178299FF95A46C623DBC260F997E58FD514853DD40`。
- Arduino AVR Boards **1.8.8**：[官方索引](https://downloads.arduino.cc/packages/package_index.json)。核心归档 SHA-256：`A234C2A43DCD01CE54BE665806F183B8E6EC4E966D2E3E0C3358B63023D6390C`。
- 核心索引指定 AVR GCC **7.3.0-atmel3.6.1-arduino7**、avrdude **8.0.0-arduino1**、arduinoOTA **1.3.0**；组件清单记录精确版本，所有实际文件纳入 SHA-256 索引，下载归档由 CLI 核验索引校验和。
- ESP32 使用独立 `arduino.esp32/3.3.8` 组件：[官方发行](https://github.com/espressif/arduino-esp32/releases/tag/3.3.8)、[官方索引](https://espressif.github.io/arduino-esp32/package_esp32_index.json)。核心 ZIP SHA-256：`8159CEFAA2EEA131360AC32393D741D462583297C8C1E5169CA48139FA324173`；GCC **14.2.0 / 2601**、esptool **5.2.0** 和各目标 SDK 按核心索引锁定，不混用原生 ESP-IDF 工具组件。
- 组件包含 `sources.json`、CLI 的 GPL-3.0 许可证及官方 AVR/GCC 工具归档中的原有版权与许可文件。实际再分发需一并保留对应开源来源与许可证，不能把外部工具称为 StudioX 自研。

CLI 的 data、downloads、user 和 build cache 都显式指向独立用户数据目录。只读工具组件提供固定 seed；CLI 不写入工具组件，不读取全局 Arduino 偏好。私有核心副本执行前逐文件核验；全局 `ARDUINO_*` 环境变量不会覆盖工程配置。

`tools/Prepare-ArduinoEsp32Runtime.ps1` 准备 ESP32 组件，跳过驱动及安装脚本，保留来源和许可证，剔除 CLI 本机安装标识。首次建立私有副本显示进度；完整校验成功后，同进程仅在全部文件集合和元数据一致时复用校验结果，重启、文件变化或新增文件会重新核验。

ESP32 的 GNU 链接器仍有 Windows 字符路径限制，因此在独立用户数据目录的兼容临时目录执行编译，完成后把完整构建记录复制回工程 `.build`，清除临时执行目录；原工程位置保持不变，不建立目录链接。私有工具路径如有空格或非 ASCII 字符则使用同一目录的 Windows 短名称；工具目录无法取得兼容名称时给出明确路径诊断。

`tools/New-ArduinoAvrPack.ps1` 使用本轮源码构建的 CLI 制作 StudioX 格式 1 的 ATmega328P 目录包；旧安装版 CLI 尚不识别新增 AVR 目录约束。发布脚本将其归入 Microchip，不改变产品版本号。

## 工程与失败边界

- `.studiox/project.json`：实际选择的器件包身份/内容摘要、芯片、Arduino 工程类型、板型和完整库版本集合；此前直接创建的 AVR 工程继续可读。
- `.studiox/arduino.lock.json`：工具内容指纹与库源码摘要。库名和版本相同但内容不同仍拒绝使用。
- `.studiox/arduino-build.json`：成功编译的源码/工程摘要、依赖指纹、主固件和配套镜像摘要。
- `.studiox/arduino-upload.json`：工程独立的 COM 端口选择；不参与源码摘要，更改端口不作废已编译的固件。
- `.build/arduino/<运行编号>`：实际 ELF/HEX/BIN、构建目录和原始日志。AVR 只上传普通 HEX。ESP32 的主 BIN、分区表和配套 bootloader 纳入同一确认，逐项建立独立快照并复核摘要后交给 CLI 的 `--input-dir`，不把单个 BIN 当作完整固件。

编译开始撤销旧上传凭据；失败、取消和输入变化不会保留旧固件的可上传状态。编译前后和上传前重新核对源码与依赖。上传使用已确认 HEX 的独立快照，并通过 `DeviceHub` 取得同一串口的独占预约。

工具准备前后保留工程配置一致性检查；准备期间更换板型不能产生旧板型的新凭据。上传确认绑定完整构建凭据，即使重新构建得到相同 HEX 字节，也不能沿用前一次确认。上传进程在独立快照目录启动，不读取工程目录中新出现的默认 profile。

搜索库允许访问网络；已完整准备的锁定依赖可以离线编译。恢复功能安装全部精确版本并比对已有内容锁，不用最新版替换旧库。安装期间工程配置发生外部变化时拒绝覆盖；原始 CLI 诊断保留。

`.ino` 使用 C++ 高亮、clangd 补全、悬停与核心/库只读跳转。AVR 使用固定核心配置，ESP32 由锁定 CLI 生成真实编译数据库并复用现有 Espressif 参数转换器。RISC-V 使用相应架构；Xtensa 使用通用 32 位解析，目标 ABI 和 GCC 专有语义以官方编译为准。多 `.ino` 拼接和自动原型同样以 CLI 为准，需要时显式声明函数。当前不开放 Arduino 源码断点调试。

## IDE 本体源码改动

这不是仅安装插件即可启用的功能。当前插件 SDK 不提供完整构建后端注册点，因此需要以下本体改动：

| 层 | 改动职责 |
| --- | --- |
| Engine | 新增 Arduino 工程/板型/库模型、创建与导入、固定运行环境、编译、依赖事务、上传预检；接入 `ProjectKind`、`ProjectManifest`、`BuildService` 和工具组件用途校验 |
| Application | 新增 Arduino 用例服务，统一串口所有权；接入组合根、工程健康检查、工程详情及 clangd 编辑配置 |
| Desktop | 新建/导入/库管理菜单和窗口；接入已有编译、下载按钮，显示 `.ino` 高亮，隐藏不适用的 OpenOCD/CMake/断点入口；新增隔离 WPF 验证入口 |
| tools / docs | 固定工具准备脚本、Arduino 专项行为验证及说明；不改产品版本，不自动打包、安装或推送 GitHub |

统一模板阶段还涉及本体的 Arduino 平台目录、实际芯片/器件包创建记录、ESP32 多镜像凭据与私有核心校验复用；Application 模板适配目录、健康检查和语言配置；Desktop 统一创建页及上传说明。

## 验证

`tools/StudioX.ArduinoValidation` 在隔离目录验证三个明确板型的真实编译、中文与空格路径、导入保留、旧产物拒绝、取消、串口预约、官方库搜索/固定版本安装/恢复和真实 clangd。温湿度库仅用于验证第三方库构建，并不代表 AHT30 已完成驱动或实板验收。

`StudioX.ArduinoValidation.exe --upload-only <新证据目录> <runtime>` 使用复制后重新索引的测试组件、真实 AVR GCC / Arduino CLI 和上传替身，验证端口持久化、断开拒绝、串口所有权、实际上传调用、空成功拒绝、原始失败日志、取消清理及板型修改后旧固件拒绝。此测试不访问物理串口。官方 AVR recipe 另用 CLI `upload --dry-run --verbose` 核对真实 avrdude 命令，不把替身结果当作实板通过。

`MCU StudioX.exe --preview-arduino <新证据目录> <已成功编译的Arduino工程> <隔离runtime>` 检查真实工作台及深浅主题窗口，不执行上传。维护时还需运行公共回归以及编辑/环境领域必跑检查，具体结果以本次报告为准。

`tools/StudioX.ArduinoTemplateValidation` 验证统一模板目录、多芯片真实编译、完整镜像预检、跨芯片选择拒绝和 ESP32 clangd。`--preview-arduino-templates <证据目录> <runtime> <AVR包> <ESP包目录>` 验证真实 WPF 中的框架切换与选择保留，不连接硬件。

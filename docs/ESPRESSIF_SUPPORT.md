# Espressif 原生 SDK 支持

## SDK 与目标

ESP32-WROOM-32、ESP32-P4、ESP32-S3、ESP32-C3、ESP32-C5、ESP32-C6 使用固定版本 **ESP-IDF 5.5.4**。ESP32-WROOM-32 是模块，原生 SDK 的目标为 `esp32`。工程保存 SDK 身份和目标，构建调用该 SDK 自带的 `idf.py`，由原生组件系统、Kconfig 和链接配置生成产物。

ESP8266 不属于 ESP-IDF 5.5.4 的目标。它使用独立的 **ESP8266 RTOS SDK 3.4** 配置及工具集，不冒充 ESP-IDF 5.5.4。两个 SDK 的工具锁定和 Python 运行环境分别校验。

通用 SoC 工程没有确定的板载 Flash 或外接 RAM 容量。新建工程的内存信息明确显示由 `sdkconfig`、链接配置与构建结果决定；ESP32-WROOM-32 的 4 MiB 物理 Flash 有模块资料支持。下载检查 SDK 实际生成的容量和镜像范围，有明确模块物理容量时同时检查该上限。

SDK 和工具链作为共享的内置组件存放，工程中只生成原生入口与用户组件，不向每个工程复制 SDK。此组件包含多种架构编译器、Python 依赖和 SDK 源码，占用数 GiB；多个工程复用一份已校验的组件。缺少组件时显示内置工具集缺失，不退回开发机的未锁定工具链。

## 模组与存储规格

打开当前工程的「器件与模板」，在编译参数上方配置 **ESP 模组与存储**。芯片目标仍由工程锁定；模组规格单独保存，选择不同存储规格不会把 `esp32s3` 改成另一个 SDK 目标。

所有已支持的 ESP 系列均提供对应型号预设和自定义板卡配置：经典 ESP32、ESP32-P4、ESP32-S3、ESP32-C3、ESP32-C5、ESP32-C6，以及独立 SDK 的 ESP8266。预设以官方完整料号为标识，保留 Flash、PSRAM、总线类型和适用的芯片版本或核心配置；选项按当前目标的原生 SDK 能力提供。温度等级、天线与封装等不改变存储参数的差异记录在型号说明中，不生成未经资料支持的后缀组合。

例如，ESP32-S3-WROOM-1-N8R8 和 N16R8 分别使用 8 MiB、16 MiB Quad Flash，两者均为 8 MiB Octal PSRAM。WROOM-2 的 N16R8V 使用 Octal Flash，不能仅凭 `R8` 判断 Flash 模式。选择预设会填入对应参数；自定义板卡需按实际器件资料选择容量与模式。配置中的 PSRAM 容量表示选定硬件规格，不代表已经读取或验证板上 PSRAM。

显式频率选项使用当前入口已支持的稳定组合。120 MHz 涉及 HPM、实验开关或 Flash/PSRAM 共享时钟约束，暂不作为无条件选项；已有工程可以沿用原生 `sdkconfig` 中的高频设置。

点击保存后，设置写入当前工程 `.studiox/espressif-module.json`，下次构建由内置 SDK 应用。沿用 `sdkconfig` 时使用用户工程配置；启用规格覆盖时，构建在 `.build` 中准备独立的 SDK 配置，仅覆盖所选硬件相关键，保留用户根目录的 `sdkconfig`、`sdkconfig.defaults` 与组件 CMake。恢复沿用工程配置不会把预设写回用户文件。

已配置的工程若使用自定义 `SDKCONFIG` 路径，覆盖以原生构建描述中的实际配置为基础，保留该文件中的其他选项。首次未配置的自定义路径需要先选择「沿用 sdkconfig」并保存，执行一次编译以取得实际路径，再选择模组；路径尚未核实时会明确拒绝覆盖。

启用 PSRAM 时，构建在锁定版本 SDK 的项目初始化阶段追加 `esp_psram` 组件，保留工程已有的组件根。官方精简示例也会加载所需的 PSRAM 配置与驱动；不会为此修改 SDK 或用户的组件 CMake。

保存或外部修改规格后，旧构建凭据和内存结果失效。重新配置时核对 SDK 实际接受的 Flash、PSRAM 与目标参数，下载继续使用原生生成的多映像布局。选择规格不会连接板卡、读取芯片、烧录或修改 eFuse。

主要规格来源：

- [ESP32-WROOM-32E / 32UE](https://www.espressif.com/sites/default/files/documentation/esp32-wroom-32e_esp32-wroom-32ue_datasheet_en.pdf)、[WROVER-E / IE](https://www.espressif.com/sites/default/files/documentation/esp32-wrover-e_esp32-wrover-ie_datasheet_en.pdf)、[SOLO-1](https://www.espressif.com/sites/default/files/documentation/esp32-solo-1_datasheet_en.pdf)
- [ESP32-P4 新版](https://www.espressif.com/sites/default/files/documentation/esp32-p4_datasheet_en.pdf)、[早期 v1.3 修订](https://documentation.espressif.com/esp32-p4-chip-revision-v1.3_datasheet_en.pdf)
- [ESP32-S3-WROOM-1 / 1U](https://documentation.espressif.com/esp32-s3-wroom-1_wroom-1u_datasheet_en.html)、[WROOM-2](https://documentation.espressif.com/esp32-s3-wroom-2_datasheet_en.html)
- [ESP32-C3-WROOM-02 / 02U](https://documentation.espressif.com/esp32-c3-wroom-02_datasheet_en.html)、[MINI-1 / 1U](https://documentation.espressif.com/esp32-c3-mini-1_datasheet_en.html)
- [ESP32-C5-WROOM-1 / 1U](https://documentation.espressif.com/esp32-c5-wroom-1_wroom-1u_datasheet_en.html)
- [ESP32-C6-WROOM-1 / 1U](https://documentation.espressif.com/esp32-c6-wroom-1_wroom-1u_datasheet_en.html)、[MINI-1 / 1U](https://documentation.espressif.com/esp32-c6-mini-1_mini-1u_datasheet_en.html)
- [ESP8266 WROOM-02D / 02U](https://documentation.espressif.com/esp-wroom-02u_esp-wroom-02d_datasheet_en.html)

## 编程与构建

- ESP32 的 Hello World 和 FreeRTOS 模板采用 IDF 5.5.4 官方示例，保留 `main/` 下的原始源码、组件 CMake、依赖和示例配置。IDE 仅记录工程名称、明确目标和工具锁定信息，首次打开使用创建记录中的入口文件。
- 原生组件通过 `idf_component_register` 声明源文件和依赖，根 CMake 入口引用共享 SDK。ESP8266 继续使用独立 SDK 的工程结构。
- 新建和后续构建保留用户组件、`sdkconfig`、`sdkconfig.defaults` 及分区配置。SDK 路径由应用工具服务提供，不写入开发者的绝对路径。
- 使用内置 SDK 完成真实配置和构建；底部保存原始 SDK、CMake、Ninja 和编译器诊断。
- 成功构建写入 `.build/studiox-build-receipt.json`，固定源码与 SDK 配置、工具指纹、完整下载布局、每个 BIN 的 SHA-256，以及应用 ELF 的路径与 SHA-256。失败和取消不使用旧凭据。
- 源码凭据覆盖工程内的代码、组件配置、图片、字体、嵌入资源和预编译库。构建同时检查 CMake 与 Ninja 的实际输入；工程外的组件、源码、头文件、库和资源只有位于已锁定 SDK/工具组件中才被接受。其他外部输入须先复制到工程，否则不能签发下载凭据。
- 编译数据库产生后，代码索引服务读取原生 SDK 的源文件、头文件和配置。ESP8266 使用自己的原生构建元数据。

内存统计读取 SDK 官方工具的原生报告，按 SDK 规则处理共享 RAM 区域。它展示链接后的静态占用，不能代替运行时堆、任务栈峰值或 PSRAM 实测；未声明的板载容量显示“未知”。

ESP 默认仅展示 Flash 和 RAM 总览，可展开“详细分组”查看原始统计组；总览复用 SDK 已归并的物理区域，避免把共享 RAM 地址别名重复计数。

Windows 下原生工具优先使用同一目录的 ASCII 短路径。带空格的目录在存在有效短名称时可以构建；若中文目录或关闭短名称的磁盘无法提供有效别名，IDE 会明确报出路径约束。请将 ESP 工程放在简短的 ASCII 路径中，不会自动搬动用户工程。

## 串口下载

工具栏下载设置为 ESP 显示专用的 **COM 端口与波特率**。刷新列表只列举端口，不自动选择。设置保存在当前工程 `.studiox/espressif-flash.json`，不会因为打开工程或编译而连接硬件。

点击下载先保存和编译，再显示实际 SDK 布局：准确目标、选择的 COM 端口、Flash 模式、频率、容量，以及每个映像的地址、字节数和 SHA-256。用户确认之后才启动内置 `esptool`。写入命令固定芯片与端口，不调用 `idf.py flash`，避免下载隐式再次构建。它按映像所在的擦除扇区写入，随后以同一套 Flash 参数独立校验全部映像并复位运行。

SDK 生成的 `flasher_args.json` 是数据，不是待执行的参数脚本。下载服务拒绝工程外路径、重解析点、重复 JSON 字段、重复或重叠映像、共享擦除扇区、容量越界、不一致芯片、额外写入选项和加密配置。当前入口不提供整片擦除、强制写入、安全启动部署或 eFuse 修改。

下载计划和授权后执行都核对原生构建描述中的实际 `config_file`、工程 `sdkconfig` 与生成的 bootloader 配置。启用安全启动、Flash 加密或 bootloader 版本防回退的工程会被拒绝，因为这些固件在首次复位时可能永久修改安全 eFuse；JSON 中未标记加密也不能绕过检查。芯片的 `_SUPPORTED` 能力标志不视为启用部署。

串口采集和下载共用连接所有权。串口终端或绘图正在占用该 COM 口时下载明确拒绝；下载预约端口后采集也不能再打开该口。取消会终止工具进程树并释放预约。原始日志保留在 `.build/esp-download-<会话>/esptool.log`；临时 BIN 快照在进程退出后删除，避免累积旧版本固件副本。

下载成功需要全部映像的校验证据，不能只凭进程退出码判断。现阶段完成的是软件和离线验证，尚未连接 ESP 实板验收。通用串口终端和绘图可以继续使用；原有 ARM/WCH OpenOCD 调试配置不套用到 ESP，ESP JTAG、USB 调试和专用 RTOS 调试需在后续确定板卡连接后适配。

## 内置 Agent 与外部 MCP

两个客户端共用 `firmware_download_plan` 与 `firmware_download`。

`project_info` 的 `espressifModule` 返回当前工程已保存的模组配置，供内置 Agent 和外部 MCP 客户端读取。芯片目标与模组规格分别返回；空配置表示沿用 SDK，不能据此推断板卡实际容量。

ESP 下载计划接受明确的 `port` 和 `baudRate`，也可以只读取已保存的工程端口。返回 `probeId="esptool"`、`speedKhz=0`、准确器件、目标、Flash 参数、全部映像及其哈希。`imageSha256` 在 ESP 中是 **整个 SDK 下载布局的 SHA-256**，同时固定每个 BIN 的地址与哈希。

执行时必须复述计划中的器件、布局哈希、`probeId`、`speedKhz`、COM 口和波特率。宿主为该次写入单独请求授权，授权后再次核对源码、配置、工具与每个映像，然后创建快照并预约端口。MCP 不接受任意固件路径，不自动编译，也不能扩大为擦除或 eFuse 操作。SDK 配置启用 stub 时，授权说明会明确告知临时装载 RAM 下载器。

## 离线验证

`dotnet run --project tools/StudioX.EspressifModuleValidation -- <runtime> <official-projects> <new-output> offline,s3,c5,p4,classic,legacy,focused`

模组检查覆盖全部预设的身份与持久化、非法组合、真实 SDK 配置和构建、原始配置保留、精简模板与显式组件根、下载布局及旧凭据失效。配置或构建通过不表示已验证实板 PSRAM。

`MCU StudioX.exe --preview-espressif-module <new-output> <official-s3-project>`

界面检查在七个目标的小型副本上操作真实 WPF 控件，覆盖预设、自定义、保存/重载、撤销、沿用配置、实时编辑区刷新、深浅主题和窄窗口；不会编译或访问硬件。

`dotnet run --project tools/StudioX.McpValidation -- --espressif-module <official-s3-project>`

使用真实 MCP 客户端与服务端验证 `project_info` 的模组字段、无配置沿用及非 ESP 工程的空值，不执行写入工具。

`dotnet run --project tools/StudioX.EspressifFlashValidation -- <new-output> [runtime isolated-built-project]`

基础检查覆盖各目标原生布局、8266 旧版 schema、全部哈希与校验证据、路径与参数边界、容量、加密拒绝和串口所有权。可选的已编译工程用假进程验证写入/校验顺序、失败、超时、截断、取消、审批变化以及快照清理；不会启动 esptool 或接触硬件。

`MCU StudioX.exe --preview-espressif <new-output> <isolated-built-project>`

检查专用下载入口、无自动 COM 选择、实际多映像确认内容、深浅主题及忙碌/关闭状态，输出 WPF 截图。不会点击写入。

## 官方资料

- [ESP-IDF 5.5.4 编程指南](https://docs.espressif.com/projects/esp-idf/en/v5.5.4/esp32/index.html)
- [ESP-IDF 5.5.4 下载布局模板](https://github.com/espressif/esp-idf/blob/v5.5.4/components/esptool_py/flasher_args.json.in)
- [ESP8266 RTOS SDK 3.4 下载布局模板](https://github.com/espressif/ESP8266_RTOS_SDK/blob/v3.4/components/esptool_py/flasher_args.json.in)
- [esptool Flash 写入与参数](https://docs.espressif.com/projects/esptool/en/latest/esp32/esptool/basic-commands.html)
- [esptool 4.11 校验实现](https://github.com/espressif/esptool/blob/v4.11.0/esptool/cmds.py)

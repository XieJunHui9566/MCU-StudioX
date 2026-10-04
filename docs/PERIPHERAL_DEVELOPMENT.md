# 外设开发辅助

产品入口：工具 → 工程与构建 → 外设开发辅助，同时进入命令面板和源码右键菜单。

当前优先 ESP-IDF，不复制 STM32 CubeMX 的配置功能。首批适配 SDK 5.5.4 / 6.1.0，覆盖 GPIO 输出、UART、I2C 主机总线、SPI 主机总线、ADC oneshot、LEDC PWM、esp_timer、RMT TX。模板负责初始化、释放及错误返回，不生成未经核对的板级连线或协议设备参数。

应用层 `PeripheralDevelopmentService` 通过 `IPeripheralDevelopmentProvider` 分派框架适配，桌面仅展示表单/代码并应用编辑缓冲区变更。后续框架或外设可在提供者中增加，不把 SDK 解析和生成逻辑放入 WPF。

## 添加到 ESP-IDF 工程

打开工程内已参与编译的 C 源文件，选择外设并填写参数，点击“添加到工程”。窗口会预览代码及将补齐的组件；“查看组件配置修改”默认折叠，可展开查看当前组件的 CMake 修改前后文本。常规操作无需手工编辑 CMake，也不新增通用图形化工程配置页面。

源码和依赖一起进入未保存缓冲区，保留当前源码、CMake 未保存修改、原有依赖、注释、换行及保存编码。使用 Ctrl+Shift+S 保存全部；后续构建或重新配置沿用现有流程生成实际编译数据库。菜单、源码右键和命令面板提供“撤销上次外设添加”，一起恢复添加前源码和依赖；若相关文件已有后续修改、关闭、只读或磁盘变化，整体撤销会拒绝，避免覆盖新内容。单文件仍可使用自己的 Ctrl+Z/重做。

自动添加只接入最近的用户组件入口，要求一个文件顶层的 `idf_component_register`，以及能证明当前源码归属的字面量 `SRCS` 或 `SRC_DIRS`/`EXCLUDE_SRCS`。同时读取 `REQUIRES` 与 `PRIV_REQUIRES`，仅补齐缺少的私有依赖；已有公开依赖不再重复添加。注释和括号字符串中的注册文本不会被误认。

源码/依赖中的变量、生成表达式、条件或函数内注册、多注册入口、未登记或被排除的源码会明确阻止“添加到工程”，源码不会单独插入。详情仍列出所需依赖，可复制后核对具体组件配置。器件资源、托管下载组件、构建目录和链接文件不通过此入口修改。预览、应用均不运行用户 CMake 或连接硬件。

## 证据与限制

- 工程 Manifest、包内器件/模板、工具组件身份、SDK 实际 `version.txt` 及目标 `soc_caps.h` 须匹配。
- 读取所需头文件、预期 API 与组件 CMake 入口。缺失、超限、能力未声明或版本不匹配时明确拒绝；不会寻找系统 SDK 或切换版本。
- 辅助读取不执行 SDK 工具，不代替工具执行前的完整发行哈希校验。共享工具租约阻止读取期间的组件维护。
- GPIO 从 SDK 数量限制输入；芯片有效输入/输出能力由生成代码中的 SDK 宏进一步检查，ADC 用 SDK 映射 GPIO → 单元/通道。窗口不推断模块引出、Flash/PSRAM/USB/控制台占用或跨实例资源冲突。
- 参数只接受范围内十进制整数和短 ASCII 名称，名称加 `sx_` 前缀。没有默认板级引脚；同一外设内部引脚须唯一。
- 预览绑定 SDK 头文件/能力/组件入口证据及完整工程身份。插入前重新读取；编辑器版本、只读状态、当前工程和文档切换均由通用插入机制复核。
- 代码插入 C 文件顶部，只修改未保存缓冲区。重复实例名称拒绝。ESP-IDF 组件依赖自动补齐并支持整体撤销；复杂配置提供具体说明。
- 每个实例单任务拥有；多模板资源需自行分配。释放失败保留资源状态，回滚记录清理错误并返回原始初始化错误。

## 验证

基础构建：`tools/Build.ps1 -Configuration Release -BuildArtifactsDirectory <独立构建目录>`。

边界验收：运行 `StudioX.PeripheralDevelopmentValidation <新输出目录>`。该模式只创建小型伪 SDK，检查源码归属、CMake 注释/引号/列表、依赖幂等、未保存合并、过期/只读拒绝与编码保留，不声称 SDK 编译通过。

原生验收：运行 `StudioX.PeripheralDevelopmentValidation <新输出目录> <已有 runtime> <已有 pack 输入目录>`。输入包括 `espressif.esp32c3-0.1.1.mcupack` 和 `espressif.esp32c3-0.4.0.mcupack`，通过正式应用服务在隔离工程添加八种外设代码并自动合并组件依赖，由现有内置 SDK 真实编译并链接，保留原始日志。不会下载 SDK、运行固件或启动硬件连接。

桌面验收：`MCU StudioX.exe --preview-peripheral-development <新输出目录> <原生验收工程> <已有 runtime>`。检查暗/亮主题、空参数、越界、重复引脚、折叠配置详情、缺失文件状态、源码/依赖未保存应用、整体撤销、保留既有未保存配置、后续编辑拒绝整体撤销、过期配置阻止部分插入，以及单文件撤销/重做，并保存截图。

实时诊断验收：`StudioX.PeripheralDevelopmentValidation --diagnostics <新输出目录> <已有 runtime> <原生验收的 native-projects 目录>`，使用真实 clangd 逐个解析两版 SDK 的八种生成源码，要求每个文件收到当前快照的完整诊断且没有错误。

默认回归包含外设边界验收；指定 `-PeripheralRuntime` 和 `-PeripheralPackInputs` 时增加两版原生编译、实时诊断及桌面验收。软件验证不代表实板外设验收。

API 来源以工程锁定的本机 SDK 头文件为准。官方说明：[I2C 5.5](https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32c3/api-reference/peripherals/i2c.html)、[LEDC 5.5.4](https://docs.espressif.com/projects/esp-idf/en/v5.5.4/esp32c3/api-reference/peripherals/ledc.html)、[RMT 6.1](https://docs.espressif.com/projects/esp-idf/en/v6.1/esp32c3/api-reference/peripherals/rmt.html)。

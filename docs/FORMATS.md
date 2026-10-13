# StudioX 原生格式 1

所有版本字段都是各自格式的版本，产品 `0.2.0` 与历史 VS Code 插件 `0.17` 无关。旧产品包/项目不做隐式迁移。

## 芯片包 `.mcupack`

ZIP 的根包含 `manifest.json`、`files.sha256.json` 与器件资源。索引是相对路径到 SHA-256 的 JSON 映射，覆盖所有文件（索引本身除外）。不用目录条目。导入拒绝绝对路径、`..`、反斜线、Windows 保留名、重名/大小写冲突、符号链接、未索引文件、内容哈希不符和超限数据。单文件 64 MiB、总量 512 MiB、最多 10000 项。

清单由 `StudioX.Packages/PackManifest.cs` 和 `DeviceDefinition.cs` 定义：

- 包：formatVersion=1、id、version、displayName、vendor、devices。
- 器件：明确型号 id、displayName、architecture（arm/riscv/mcs51）、flashOrigin/flashBytes、ramOrigin/ramBytes。
- 可选 `monitorFirmware`：精确 id/displayName/version/protocol、包内 imageFile/provenanceFile、imageSha256/imageBytes、适用 bootloaderVersion/bootloaderStatus。镜像和来源文件都受包路径、索引及大小限制；额外核对实际镜像长度和 SHA-256。旧包可不声明；声明不表示任意调试后端都支持它。Mon51 制作后端进一步固定已验证的准确器件、版本、状态和镜像身份，不将包声明作为擦写授权。
- 构建：toolsetId/toolsetVersion/compilerId、cpuFlags、defines、includeDirectories、sources、linkerScript、compileOptions、linkOptions。
- 器件与模板可选 `developmentComponents`：精确 `id/version/compilerId/host/purpose`，当前仅 win-x64，最多 64 项。器件需求包含主工具的相同身份，模板需求叠加；相同身份去重，冲突拒绝。不声明时沿用原三个工具字段，不改写旧包。详见 [开发环境组件](DEVELOPMENT_COMPONENTS.md)。
- 模板：id、displayName、description、entryFile；入口固定生成 `src/main.c`。可选 `build` 叠加 defines/includeDirectories/sources/compileOptions/linkOptions；构建和语言服务采用同一解析结果。可选 `files` 将用户目标相对路径映射到包内源文件，只允许 `src/`、`include/`，不能覆盖 main.c 或工程元数据。
- AGM 模板可选 `ag32Sources: { pinMapFile, verilogFile, entryFile?, build? }`，仅在用户创建工程时勾选特殊模式才启用。前两者复制到 `logic/pins.ve` 和 `logic/user_logic.v`，可选入口替换初始 `src/main.c`，可选 `build` 叠加同格式构建参数；所有来源仍受包边界校验。未提供则沿用逻辑编辑骨架。模板不能强制勾选或禁用特殊模式。
- 模板可选 `replacesTemplates` 声明明确合并的旧模板 ID，不能与本版任何模板 ID 重复。仅用于新版同 ID 包的选择器替代判断，不重写或迁移已建工程的模板与资源。
- 下载：器件可选 `openOcd`，含包内 `targetScript` 和 `probes`。每项定义 id/displayName/interfaceScript/transport/defaultSpeedKhz，接口脚本相对内置 OpenOCD 的脚本目录。目标脚本必须提供 `studiox_check_target`，身份检查通过之后才允许写入。
- 应用范围：`openOcd.applicationFlashBytes` 可选，必须大于 0 且不超过 `flashBytes`。BIN 大小及 ELF 物理装载段限制在 `[flashOrigin, flashOrigin + applicationFlashBytes)`；省略时沿用物理 Flash 范围。AG32 用于保留逻辑区，目标脚本还需检查实际选项字节布局，不能仅靠主机清单推断安全范围。

资源可以包含启动代码、头文件、厂商源码、链接脚本、SVD、烧录配置及模板。编译器和构建工具不得塞入芯片包。包不是插件，导入时不执行代码；后续用户选择构建时才编译源码。完整性哈希不是来源签名。

仓库布局：`<user-data>/packs/<id>/<version>/payload`，旁边是安装记录与内容索引。相同版本不同内容拒绝覆盖。选择器使用轻量目录，只读取安装记录、索引及清单并核对索引摘要、清单 SHA-256 和目录身份，不遍历 SDK。创建工程前重新完整校验选中的包（包括文件集合、全部文件哈希及资源定义），拒绝选择后被替换的内容；`ListAsync` 仍保留完整校验语义。导入校验保持不变。完整性哈希不是发布签名。

## 工程

MicroPython 模板在普通格式 1 器件包的模板项中声明 `microPython: { board, version }`。当前仅接受与 RP2040-PICO / RP2350A-PICO2 对应的 RPI_PICO / RPI_PICO2、1.29.0。它不能同时声明 C 构建覆盖或 Espressif 示例；入口必须是 `.py`。其附加用户文件允许 `boot.py`、`README.md` 和 `lib/*.py`，路径仍经过包边界校验。

选择这类模板后，工程 `kind` 为 `MicroPython`，入口为 `main.py`，开发环境组件与编译器字段为空，并保存明确的 `microPython` 配置。工程仅复制身份清单和所选脚本；打开时校验板型、版本、模板与 `device/manifest.json` 一致。C 编译、OpenOCD 和 GDB 后端拒绝这种类型。原 `Pack`、CubeMX、Zephyr 工程不得夹带 MicroPython 配置。

`.studiox/project.json` 保存格式 1、工程名、芯片包 ID/版本/内容哈希、明确选择的芯片/模板和开发环境组件 ID/版本/compilerId。复制器件资源到 `device/`，模板入口放入 `src/main.c`，生成用户可维护的 `CMakeLists.txt`。

含 `template.build` 的工程根据所选模板筛选 `sdk/`：仅拷贝编译源文件和包含目录内资源；模板用户文件放到 `src/`、`include/`，追加到根 CMake。原始 `device/manifest.json` 和包索引保留来源信息，工程不是可重新导入的包镜像。仅含入口的早期模板继续完整复制器件资源。

`.studiox/download.json` 保存烧录器 ID、速度（kHz）和可选序列号。下载服务使用当前开发环境组件、构建版本锁和固件快照，日志与实际写入副本保存在 `.build/download-<id>/`。不支持下载的包保持按钮禁用。

首次构建生成 `.studiox/toolchain.lock.json`，记录精确开发环境组件 ID、版本和清单指纹。工程不保存开发机器的绝对工具路径；CMake 通过调用参数获取当前安装路径，机器相关缓存只保留在 `.build/`。项目本身就是受用户控制的源码，修改 CMake 后编译将执行对应构建规则。

新工程将选中器件/模板的 `developmentComponents` 需求快照保存到工程清单。首次构建完整校验所有组件后建立 `.studiox/development-components.lock.json`，格式为 `{ formatVersion: 1, components: [{ id, version, host, compilerId, fingerprint }] }`。原主工具锁保留，额外锁不能替换或绕过它。需求与显式器件声明、所有内容锁须一致；锁不存在时健康检查只提示首次构建建立，不自行创建。缺失组件或锁不一致时在工具启动前报错，不自动选择更高版本。

## 开发环境组件

开发环境组件离线归档使用 `.mcutoolchain`，默认真实 7z/LZMA2 容器；签名识别兼容既有 ZIP 及 `.studioxtools`。只含普通文件，根目录原始 `toolset.json` 的字节和指纹不随压缩格式变化，`sha256` 覆盖全部其它文件。最多 200,000 项、展开总量 40 GiB、清单 32 MiB；拒绝链接、目录、危险路径、加密和不完整文件集合。IDE 自带读写器，完整/轻量版均支持；器件 `.mcupack` 仍使用原有 ZIP 格式。

`runtime/toolsets/<id>/<version>/toolset.json` 定义格式 1、ID/版本、host=win-x64、compilerId、executables、sha256。必需角色：gcc/gxx/objcopy/size/cmake/ninja。当前发行还包含 gdb/ar/ranlib/as/ld/objdump/readelf/openocd；OpenOCD 已接到包定义的一次性下载流程，GDB 调试会话待接入。

可选字段 `displayName` 为显示名称，`componentVersions` 保存组件实际版本输出，`resourceDirectories` 为资源角色到相对目录的映射（`openocdScripts` 指向匹配版本的 OpenOCD 脚本）。完整性检查包括这些目录及其中所有文件。`provenance.json` 记录来源与实际版本，不包含开发者安装路径。

sha256 覆盖清单之外的完整工具树；清单同时受工程的版本锁指纹约束。开发环境组件版本指发行组合版本，编译器/组件原始版本及许可证应记录在发行资料中。`examples/toolsets/toolset.example.json` 只说明结构，空索引不会通过工具解析，不能当成已安装工具。

`.build/studiox-runtime.json` 是可重建的本机缓存标识，记录本次工程位置、开发环境组件位置和指纹。位置改变后用 CMake `--fresh` 重配，不修改工程声明或用户源文件；此缓存不随源码提交。

## 插件和主题

插件格式/API 均为 1，独立进程 `IFrameDecoder` 为首个扩展点，见示例目录。其它插件能力尚未开放。

主题为格式 1 JSON：id、displayName、colors。colors 必须完整提供 Background/Surface/Panel/Border/Text/Muted/Accent/OnAccent 八个 `#RRGGBB` 颜色。主题文件不能执行 XAML。选中的主题和导入的主题保存在用户数据目录，不影响工程构建。

## 采集记录 `.sxcapture`

UTF-8 JSON Lines。首行是格式 1、clockDomain=host-monotonic、ticksPerSecond、connectionKey、simulated。后续每行 sequence/timestamp/base64。时间戳来自宿主单调时钟，不代表串口线上某位或某个物理边沿的采集时刻。丢帧可表现为 sequence 的间隔；订阅层另暴露 droppedFrames。高精度硬件时序和长时间高吞吐记录需后续专门设计。

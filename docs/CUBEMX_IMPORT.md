# 导入 CubeMX CMake 工程

欢迎页、文件菜单和空工程侧栏提供「导入 CubeMX CMake 工程」。普通「打开工程」选中尚未导入的目录时，也进入导入流程。此入口独立于器件包新建工程，不需要安装 STM32 `.mcupack`。

在 STM32CubeMX 中选择 CMake 工具链并生成代码，选择包含 `.ioc` 和根 `CMakeLists.txt` 的目录，然后选择实际列出的配置预设。CMake 负责展开预设的继承、include 和条件；没有预设文件时可选择 Debug 或 Release。

当前支持单工程、ARM GCC 的标准 CubeMX 目录：根目录恰好一个 `.ioc`、`cmake/gcc-arm-none-eabi.cmake`、`cmake/stm32cubemx/CMakeLists.txt`。芯片名称取自 `.ioc`，默认打开 `Core/Src/main.c`。多核、TrustZone 多子工程入口及非 GCC 工具链尚未适配；不会根据不完整目录猜测器件配置。

导入仅新增 `.studiox/project.json`，不会复制、移动或改写原有 Core、Drivers、Middlewares、用户库、链接脚本、CMake 或 `.ioc`，也不会应用 StudioX 模板中的默认时钟。再次导入已导入的目录会直接打开它，底层服务拒绝覆盖已有元数据。CubeMX 后续重新生成代码后，重新打开或编译即可刷新构建和索引。

工程元数据使用 `kind: CubeMx` 与相对路径；工具集固定 `arm.gnu 1.0.0` / `arm-gnu-15.2.rel1`。普通包工程保持 `kind: Pack`，历史格式 1 未写 kind 时仍按 Pack 处理。工程不记录安装目录的绝对工具路径。

构建使用内置 GCC、CMake、Ninja，独立输出到 `.build`，保留原有 `build/Debug` 等目录。读取 CMake File API 的真实可执行目标，检查实际编译器属于锁定工具集；不会要求目标名为 firmware。原工程自带的 BIN/HEX 输出原样保留；缺少的格式由内置 objcopy 生成到 `.build/studiox-artifacts`。MAP 只报告已存在的文件。原始构建诊断、中文成功/失败结论和退出码保存在构建日志。

CubeMX 工具链从内置工具集的隔离 PATH 解析 `arm-none-eabi-`，配置时不再重复覆盖 C/C++/ASM 编译器路径，防止 Windows 路径大小写差异被 CMake 判定为更换编译器并丢失交叉编译设置。升级后首次配置自动重建旧版缓存；配置失败或取消后，下次也重新配置。成功配置继续复用缓存和增量编译，不修改原工程 CMake 文件。

首次打开配置 CMake 而不编译，用 `.build/compile_commands.json` 中每个翻译单元的真实宏、包含目录、CPU 参数和语言标准建立 clangd 索引。编译成功后刷新。配置失败仍可编辑源码与 CMake，并在日志中查看具体原因；不会继续使用之前工程的语言会话。

导入识别、工具链检查、CMake 配置和代码索引在后台执行，避免异步文件读取命中缓存后又把目录扫描或解析留在界面线程。状态栏显示当前阶段，首次完整校验最多每 250 毫秒更新一次文件进度；工具版本与完整性检查仍保留。编辑器首次布局和工程树展开分两轮处理，让输入与绘制有机会执行。期间可使用工具栏「停止」取消。

已接入 F1/F4 下载：与器件包工程共用顶部 ST-Link / DAP-Link / J-Link 下拉栏，按实际 ELF 目标与链接地址烧录，详见 [下载说明](DOWNLOAD.md)。调试界面尚未接入。随包 ARM Binutils 的 objcopy 对中文等非 ASCII 绝对路径存在问题，因此导入时明确提示将工程放到英文路径；空格路径已验证。工程所需 SDK/用户库必须已存在，外部机器的硬编码编译器路径需要用户修正。

实现依据：[CMake 预设和命令行覆盖](https://cmake.org/cmake/help/latest/manual/cmake.1.html)、[CMake File API](https://cmake.org/cmake/help/latest/manual/cmake-file-api.7.html)。

## 验证

`tools/StudioX.CubeMxValidation` 只复制本地工程到新的隔离目录后测试：F103C8T6 HAL Debug、F407ZGT6 HAL + LVGL Release，均生成原目标名的固件并通过重复构建；927 / 2023 个原有文件哈希不变。实际 clangd 完成 HAL 函数补全、悬停、声明跳转。未连接或烧录硬件。

边界检查入口：`dotnet run --project tools/StudioX.CubeMxValidation -- --boundaries <runtime> <new-output>`。
桌面检查入口：`MCU StudioX.exe --preview-cubemx <new-output> <isolated-imported-project>`。该入口会配置并编译传入工程，只应传入上述验证工具生成的副本。

响应检查：`MCU StudioX.exe --preview-import-performance <new-output> <isolated-imported-project>`。以 16 毫秒输入优先级计时器记录 UI 调度间隔，同时检查索引、跳转和从界面停止校验。F407 + LVGL 隔离副本的本机 Debug 对比：冷检查最长间隔约 114 → 40 毫秒，热检查约 110 → 33 毫秒，打开/配置/索引约 295 → 86 毫秒；首次识别总耗时约 11.85 → 7.29 秒。数据受文件缓存与机器负载影响，后台检查仍需等待，不代表瞬间完成。

自包含 Release 复核：冷检查最长 UI 间隔约 51 毫秒，打开/配置/索引约 111 毫秒；从 UI 请求停止至流程返回约 75 毫秒。桌面导入、编译、提示/跳转及关闭检查通过，工具集缓存和失败边界 17 项通过。

2026-09-22 缓存回归：以全小写运行时路径复现旧版 F407 + LVGL 第二次配置失败；修复后 Debug 构建、跨会话重开、重复配置和 HAL 提示/跳转通过，隔离副本 2023 个原文件哈希不变。边界检查覆盖旧版 Windows 缓存自动迁移、失败后恢复和无需重编译的增量构建。通过桌面界面打开用户原工程，自动恢复配置并连续点击两次「编译」，均退出 0；第二次 Ninja 报告 `no work to do`，界面计时约 2 秒。未进行硬件下载。

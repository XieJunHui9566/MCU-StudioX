# CMake 工程分层

本文描述从器件包新建的工程。已有 CubeMX CMake 工程使用独立的[导入流程](CUBEMX_IMPORT.md)，保持 CubeMX 的原目录和构建文件。

新建工程将用户配置和器件支持分开。组织方式参考本机 CubeMX 工程：根 CMake 管理用户代码，内部 CMake 管理生成的器件资源。现有格式 1 器件包即可生成这种布局，包和工具链版本不变。

```text
工程/
├─ CMakeLists.txt             用户维护：源码、包含目录、宏、库
├─ src/
│  └─ main.c                  用户应用入口
├─ device/                   固定器件支持，来源于所选 mcupack
│  ├─ CMakeLists.txt          SDK 源码、寄存器头文件目录、宏、CPU/ABI、链接选项
│  ├─ platform.cmake         裸机环境、ELF/BIN/HEX/MAP 产物规则
│  ├─ sdk/include/            厂商库接口与寄存器定义
│  ├─ sdk/src/                厂商实现
│  ├─ sdk/startup/            启动汇编和系统调用
│  ├─ linker/                 器件链接脚本
│  └─ manifest.json           原器件包清单
└─ .studiox/                  工程及工具锁元数据、build.json 编译参数
```

`sdk/` 的子目录由包决定；Engine 使用器件清单中的明确相对路径，不猜测厂商目录结构。用户入口从模板复制到 `src/main.c`，应用文件不放在 SDK 中。

AG32VF303CCT6 工程若在创建时启用 Verilog 逻辑模式，还会生成独立的 `logic/user_logic.v`、`logic/pins.ve` 和 `logic/README.md`；默认工程没有 `logic/`。这部分不加入 MCU 的 CMake 目标，Quartus II / Supra 与逻辑下载流程见 [AG32 Verilog 逻辑模式](AG32_LOGIC_MODE.md)。

根配置只在创建工程时生成。构建服务不会重写根配置；新增文件需要显式填入相应区域，例如：

```cmake
target_sources(firmware PRIVATE
    src/main.c
    src/uart.c
)
target_include_directories(firmware PRIVATE src include)
target_compile_definitions(firmware PRIVATE USE_UART=1)
```

自建库可以增加 `add_subdirectory(...)` 和 `target_link_libraries(firmware PRIVATE my_library)`。根文件保留 `include(device/platform.cmake)`、目标创建、`add_subdirectory(device)` 和 `studiox_configure_firmware(firmware)` 作为固定入口。

内部 `CMakeLists.txt` 创建 `studiox_device` 接口目标。固定源文件通过接口源加入固件，头文件目录、宏和编译/链接参数随目标传递，启动文件不藏进可能被链接器省略的静态归档。所有器件路径按内部文件所在目录解析，工程内不写开发机绝对路径。

`platform.cmake` 在 `project()` 前声明裸机环境。它定义的函数由根目录调用，为在根目录创建的目标注册 POST_BUILD 转换规则；不能直接在 device 子目录给根目标添加该命令。链接脚本列入 `LINK_DEPENDS`，变更后能触发重新链接；BIN/HEX 声明为副产物，MAP 纳入清理规则。

带生成器标识的这两个内部文件在 IDE 中只读，标签和底栏说明应修改根配置。其他文件继续遵守本身的文件权限；没有修改操作系统 ACL。这里的只读用于防止日常误改，不是权限隔离。器件包不得占用内部 `CMakeLists.txt` / `platform.cmake` 入口，否则创建工程报错，不覆盖包内文件。

CMake 编辑提示额外索引这两个固定生成文件中的目标和函数，不执行配置脚本，也不递归展开任意 `include`。旧工程不会在启动或构建时被自动改写。

分层配置迭代检查覆盖两种 AG32 模板的新建工程、根配置编辑、内部只读、生成符号提示、中文和空格路径，以及禁用编译语言的 CMake 配置图夹具。后续内置工具链迭代已补充实际 AG32 编译与授权后的最小固件下载，见 [工具链验证记录](TOOLCHAIN_VERIFICATION.md)。

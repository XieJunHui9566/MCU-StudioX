# CMake 工程分层

本文描述从器件包新建的工程。已有 CubeMX CMake 工程使用独立的[导入流程](CUBEMX_IMPORT.md)，保持 CubeMX 的原目录和构建文件。

工程名称支持中文、英文、数字、空格、下划线、连字符和点，最多 80 字符；首尾不能有空格，末尾不能是点，也不能使用 Windows 设备名（如 CON、COM1）或路径分隔符。中文名称保存在工程目录和 `.studiox/project.json` 中，创建与重新打开共用校验，适用于全部工程后端；器件包、组件等内部标识仍保持原来的 ASCII 规则。

普通 MCU 工程的编译目标仍为 `firmware`；ESP 原生模板需要工程标识时，为中文名称生成稳定的 ASCII 内部标识，显示名称和用户目录不变。Arduino 保留中文 `.ino` 入口，CLI 使用独立的英文 sketch 副本，产物和上传凭据按内部标识对应；分析参数与诊断映射回原始源码。PIC、Zephyr 延用独立执行目录，MX251 与通用 GCC/SDCC 保持原有 CMake + Ninja 入口。

旧版 ESP 原生工具不接受 Unicode 路径时，每个工程复用一个用户缓存下的固定英文执行目录；不改用户工程名、不依赖中文目录的 8.3 别名，也不修改 SDK。只复制变化的输入，保留增量构建；成功后发布最新产物并重建源内容凭据，编译数据库和诊断指回中文工程。构建中源码变化、SDK 意外修改非生成输入或产物不完整时拒绝生成下载凭据。SDK 生成的配置仅在原文件未变化时回写，下载前仍核对实际源码、配置、工具和全部映像。ESP8266 SDK 3.4 的组件属性序列化与 Kconfig 文件列表在生成 hook 中处理空格路径，不更改共享 SDK。

自带 MCU 模板及固件代码生成入口统一用 `while (1)` 表达无限循环；常见入口保持 `int main(void)`，PIC/XC8 等工具链要求的 `void main(void)` 仍遵守其入口约定。该风格规则不批量改写已有用户工程或厂商 SDK。

从旧器件包创建新工程时，可编辑的 `src/main.c` 同样采用该风格；仅转换空条件 `for (;;)`，保留字符串、注释和有条件循环。原器件包、`device/` SDK 及已锁定工程不改写，避免为风格修改破坏既有包指纹。

51 / 251 工程可在成功编译后并排查看带地址、机器码的链接汇编或编译器原始汇编，见 [编译汇编](BUILD_ASSEMBLY.md)。

日常产物在工程 `.build` 下复用固定目录。PIC、Arduino、自定义 AG32 逻辑使用 `.build/<后端>/latest`，不按构建哈希或随机编号累积历史；普通 CMake/Ninja 后端继续使用其固定构建目录。构建凭据负责内容一致性，不能通过保留随机文件夹代替凭据校验。下载回读备份和未完成恢复的调试资料单独保管；冻结验收证据不属于日常产物清理范围。

新建工程将用户配置和器件支持分开。组织方式参考本机 CubeMX 工程：根 CMake 管理用户代码，内部 CMake 管理生成的器件资源。现有格式 1 器件包即可生成这种布局，包和开发环境组件版本不变。

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

AG32VF303CCT6 工程默认生成独立的 `logic/pins.ve` 与 `logic/README.md`，工程清单的 `pinMapping` 锁定 AGRV2KL48 与基础映射工具。`.ve` 将 MCU 内部功能绑定到真实封装引脚，不预设开发板连线；基础映射使用内置 AGM VE / Supra，不需要 Quartus。创建时另选自定义 Verilog 会额外生成 `logic/user_logic.v`，`logic` 记录这个独立模式，不能用默认映射替代其综合结果。旧工程可显式启用基础映射并保留既有 `.ve`；工作流程见 [AG32 基础引脚映射与自定义逻辑](AG32_LOGIC_MODE.md)。

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

分层配置迭代检查覆盖两种 AG32 模板的新建工程、根配置编辑、内部只读、生成符号提示、中文和空格路径，以及禁用编译语言的 CMake 配置图夹具。后续内置开发环境组件迭代已补充实际 AG32 编译与授权后的最小固件下载，见 开发环境组件验证记录（本地记录）。

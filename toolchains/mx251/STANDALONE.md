# MX251 独立使用契约

MX251 是独立工具链。MCU StudioX 是一个宿主；编译器、运行库、目标配置和构建入口不能依赖 StudioX 服务、WPF、`.studiox/project.json` 或 StudioX 安装目录。其他编辑器、构建服务器和命令行使用相同的 CMake + Ninja 流程。

当前独立 SDK 宿主契约面向 Windows x64，尚非 LST。仓库公开通用构建模块和目标契约；完整编译器候选、运行库与装配输入暂未公开发布。下述操作要求另行取得完整、匹配且通过内容锁校验的 SDK，不代表克隆 IDE 源码即可取得编译器。Linux/macOS 未验证。

## 目录与工具

独立工具目录保持下列结构，所有路径由调用者提供，不修改系统 PATH：

```text
MX251/
  toolchain/                 # 配套汇编器、链接器、预处理器、头文件和标准库
  profiles/
    AI8051U-34K64-32/         # 目标启动、原生库、compiler/sdcc.exe、profile.lock.json
    STC32G12K128/
  build/qualified/           # 仓库中的同一组脚本与 CMake 模块
    Project.cmake
    BuildLatest.ps1
    Build.ps1
    TargetContracts.json
    ...
  powershell/                # 可随工具附带，也可显式指定外部 PowerShell 7
  licenses/                  # 分发时必须保留各组件许可证及对应源码获取说明
  sources/                   # 当前及运行库基础源码、上游源码归档、内容锁
  sdk.json                   # 装配完成凭据和所有文件的相对路径/SHA-256
```

使用完整且匹配的标准库/预处理器目录，不能只复制 `sdcc.exe`。`profile.lock.json` 锁定基础工具树、编译器覆盖、目标配置和运行库；构建时完整检查。直接运行原始编译器也不需要 IDE，但自行绕过这些目标/ABI检查的组合不继承已验收结论。

CMake 和 Ninja 可以独立提供。当前实测 CMake 4.4.0、Ninja 1.10.2；入口要求 CMake ≥3.25、PowerShell ≥7，不保证任意版本组合已验证。

## 工程入口

```cmake
cmake_minimum_required(VERSION 3.25)
project(blink NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE src/main.c)
target_include_directories(app INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/include")
# target_compile_definitions(app INTERFACE APP_FEATURE=1)
mx251_firmware(app)
```

配套 `mx251.json` 仅保存目标/ABI/内存模型/运行库，不是源码清单：

```json
{
  "schema": 2,
  "target": "AI8051U-34K64-32",
  "model": "small",
  "optimization": "speed",
  "backend": "ordinary-wide",
  "explicitAbi": true,
  "standard": "sdcc11",
  "runtime": "standard",
  "defines": []
}
```

当前还接纳 `STC32G12K128`，须选它自己的 profile；不能混用启动代码、硬件配置和运行库。已验收地址窗口见 `TargetContracts.json`，并非芯片全容量支持声明。实际芯片运行模式由下载流程确认，编译不设置 ISP。

## 从命令行构建

以下路径是用户按自己环境填写的变量。没有 StudioX 工程文件也可以执行：

```powershell
$mx = 'D:/Tools/MX251'
$cmake = 'D:/Tools/CMake/bin/cmake.exe'
$ninja = 'D:/Tools/Ninja/ninja.exe'
$project = 'D:/Projects/blink'
& $cmake -S $project -B "$project/.build/cmake" -G Ninja `
  "-DCMAKE_MAKE_PROGRAM=$ninja" `
  "-DMX251_PROJECT_MODULE=$mx/build/qualified/Project.cmake" `
  "-DMX251_BUILD_SCRIPT=$mx/build/qualified/Build.ps1" `
  "-DMX251_PWSH=D:/Tools/PowerShell/pwsh.exe" `
  "-DMX251_TOOLCHAIN=$mx/toolchain" `
  "-DMX251_PROFILE=$mx/profiles/AI8051U-34K64-32" `
  -DMX251_LEAF_CALL_FEEDBACK=OFF
if ($LASTEXITCODE) { throw 'Configure failed' }
& $cmake --build "$project/.build/cmake" --target firmware
if ($LASTEXITCODE) { throw 'Build failed' }
```

正常固件始终在 `.build/mx251/latest/firmware.hex` 和 `firmware.bin`。同目录保留 MAP/MEM、原始日志、输入快照、调用影响摘要和 `receipt.json`。BIN 从凭据的 `binary.baseAddress` 开始，空洞填 `0xFF`；地址不是烧录器配置的默认推断。只有返回码成功、`receipt.json` 状态完整且哈希匹配，才应消费产物。

目录只保留最新一次构建。归属记录在子编译器启动前写入；失败不发布成功凭据，重建会清理自己拥有的半成品。未知目录或包含目录链接时拒绝自动删除。并行构建同一工程会被输出租约拒绝。外部宿主应像 IDE 一样在配置失败/取消时撤销它自己发布的公共副本；直接 `cmake` 配置失败不会调用 Ninja 的构建目标，不能把前一次目录当成此次成功。

受限两遍叶调用反馈默认关闭。命令行可显式传 `-DMX251_LEAF_CALL_FEEDBACK=ON`；IDE 可通过工程 CMake 显式设置，删除设置后下次配置恢复 OFF。反馈输入与 ABI、工具、ASM/REL 和最终 HEX 绑定，未知边保持保守回退，不等于全程序 LTO。

## SDK 装配

仓库入口 `tools/Prepare-Mx251Sdk.py` 使用调用者提供的本地输入及单独审核的内容锁，生成上述目录；用 `--verify` 检查完整文件集合、目标 profile、编译器和运行库哈希。当前装配不附带 PowerShell/CMake/Ninja，使用者显式提供这些通用宿主工具。SDK 的构建脚本不需要 Python；Python 只用于装配与仓库验收。

源码与来源保存在 `sources`，配套工具许可证保存在 `toolchain/licenses`，SDCC 许可证及组件说明在 `sources`。`sources/mx251-source.zip` 区分当前编译器与运行库基础编译器的源码；不能把当前版本号理解为每个配套二进制都重新编译了一遍。装配方法与范围见 `docs/SDK-ASSEMBLY.txt`。

## 宿主边界

- 独立构建入口来自 `build/qualified`，随工具组件一起锁定；新版 IDE 直接调用组件内文件，不嵌入或覆盖另一套实现。历史 R89 适配保留原路径。
- `studiox.mx251` 和三段组件版本只属于 IDE 适配层。独立 SDK 不需要 `toolset.json`、器件包导入或 StudioX 账户。IDE 包装组件通过 `mx251-sdk` 资源目录指向完整 SDK，并锁定 `sdk.json`；编译器身份须与 SDK 版本一致，IDE 按 SDK schema 和构建契约校验，不内置编译器版本或哈希白名单。工程仍精确锁定所选组件版本。
- `MX251_OUTPUT_LEASE` 是宿主内部的可选输出租约传递参数，普通 CLI 不设置它。默认入口自行取得排他锁。
- 默认不枚举/烧录任何硬件。HEX/BIN 的生成、下载 ACK、运行结果和 Flash 回读分别验收。
- 将来的压缩包、安装包或其他 IDE 插件都必须复用这个入口，并保留许可证、来源及可复现源码；不能以 IDE 功能完善为由移除独立使用能力。
